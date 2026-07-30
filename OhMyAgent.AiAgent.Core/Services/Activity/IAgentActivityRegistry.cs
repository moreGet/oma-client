using System;
using System.Text.Json;
using System.Threading;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 실행 중인 에이전트 작업(턴 · 도구 · 자식 프로세스)의 단일 등기소.
///
/// 왜 Core 에 두는가: GUI 와 헤드리스가 <b>같은 오케스트레이터를 공유</b>하므로 계측 지점도 공유돼야 한다.
/// WPF 형식을 하나도 참조하지 않는다(Core 는 <c>net10.0</c>, -windows 아님 — 컴파일러가 이를 강제한다).
///
/// 계약 두 갈래:
///  - <b>계측</b>(Begin*/Track*): 오케스트레이터·도구가 호출한다. 관찰만 하며 기존 실행 흐름을 바꾸지 않는다.
///  - <b>제어</b>(RequestCancel/CancelAll/KillChildProcess/SweepOrphans): UI 가 호출한다.
///
/// "중지"의 의미를 흐리지 말 것 — 관리 스레드는 강제 종료할 수 없다. 취소는 협조적 취소이며,
/// 강제 종료는 자식 <b>프로세스</b> 에만 가능하다.
/// </summary>
public interface IAgentActivityRegistry
{
    /// <summary>
    /// 구조 변화(추가·제거·상태 전이·반복 진행)가 있을 때만 발화한다.
    /// 경과 시간처럼 "가만히 있어도 값이 변하는" 것은 여기로 밀지 않는다 — 그건 표시 주기의 문제이고,
    /// 1초마다 Core 가 UI 스레드를 깨우면 태스크 매니저를 보고 있지 않을 때도 비용을 낸다.
    /// 구독자는 UI 스레드로 마샬링할 책임이 있다(임의 배경 스레드에서 온다).
    /// </summary>
    event EventHandler? Changed;

    /// <summary>지금 이 순간의 화면 1장. 깊이 우선 정렬 + 파생값(경과·건강도·고아 여부) 계산까지 끝난 상태로 온다.</summary>
    AgentActivityView Snapshot();

    // ── 계측 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 에이전트 턴 1회를 등록한다. 부모는 <see cref="EnterOwner"/> 로 지정된 주변 소유자다
    /// (서브에이전트 턴이 자기를 띄운 <c>task</c> 도구 아래로 자동 중첩되는 경로).
    /// 반환 스코프의 <see cref="IAgentActivityScope.Token"/> 은 <paramref name="parentToken"/> 에 연결된
    /// 이 턴 전용 토큰이다 — 개별 취소가 기존 사용자 중지 경로를 침범하지 않게 하는 장치다.
    /// </summary>
    IAgentActivityScope BeginTurn(CancellationToken parentToken, int maxIterations);

    /// <summary>
    /// 도구 실행 1건을 등록한다. 승인 대기 중에도 목록에 보여야 하므로 <b>실행 직전이 아니라 시작 시점에</b>
    /// 등록한다. 반환 스코프의 토큰은 이 도구 전용이며, 이것만 취소하면 턴은 계속 진행된다.
    /// </summary>
    IAgentActivityScope BeginTool(
        Guid? parentId, CancellationToken parentToken, string toolName, ToolRisk risk, JsonElement arguments);

    /// <summary>
    /// 도구 수명 동안만 사는 자식 프로세스를 등록한다(<c>run_command</c>). 스코프를 Dispose 하면 해제된다 —
    /// <c>finally</c> 로 반드시 해제해야 고아로 잘못 분류되지 않는다.
    /// </summary>
    IAgentActivityScope TrackChildProcess(TrackedProcessIdentity identity, string? detail);

    /// <summary>
    /// 도구가 반환한 뒤에도 살아 있는 자식 프로세스를 등록한다(<c>start_process</c>).
    /// 해제 시점이 없으므로 스코프를 주지 않는다 — 정리는 (1) 종료 감지, (2) 고아 정리,
    /// (3) 추적 상한 프루닝 이 담당한다. 소유 도구가 끝나면 이 항목이 곧 "고아 후보"가 된다.
    /// </summary>
    void TrackDetachedProcess(TrackedProcessIdentity identity, string? detail);

    /// <summary>
    /// 이 아래에서 등록되는 자식 프로세스·하위 턴의 소유자를 지정한다(AsyncLocal).
    ///
    /// <b>async 반복자(<c>async IAsyncEnumerable</c>) 본문에서 호출하지 말 것</b> — yield 경계를 넘어
    /// 값이 보존되지 않는다. 평범한 <c>async Task</c> 메서드에서만 쓴다(오케스트레이터의
    /// <c>ExecuteCallAsync</c> / <c>RunCallAsync</c>).
    /// </summary>
    IDisposable EnterOwner(Guid activityId);

    // ── 제어 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 해당 항목의 취소를 요청한다(협조적). 자식 프로세스 항목에는 토큰이 없으므로 false —
    /// 그쪽은 <see cref="KillChildProcess"/> 가 유일한 수단이다.
    /// 첫 요청 시각을 기록해 "취소가 안 먹는 항목"을 드러낸다.
    /// </summary>
    bool RequestCancel(Guid id);

    /// <summary>진행 중인 모든 항목에 취소를 요청한다. 신호를 보낸 항목 수를 돌려준다(사용자 안내용).</summary>
    int CancelAll();

    /// <summary>
    /// 자식 프로세스를 강제 종료한다. <b>반드시 신원을 재확인한 뒤에만</b> 종료한다 —
    /// PID 는 재사용되므로 확인 없이 죽이면 남의 프로세스를 죽인다.
    /// </summary>
    ProcessKillOutcome KillChildProcess(Guid id);

    /// <summary>
    /// 고아 프로세스를 정리한다 — 우리가 띄웠고, 소유 작업은 이미 끝났고, 아직 우리 것으로 살아 있는 프로세스.
    /// 신원 확인에 실패하거나 PID 가 재사용된 항목은 <b>건드리지 않고</b> 추적만 버린다.
    /// </summary>
    OrphanSweepResult SweepOrphans();
}

/// <summary>
/// 등록된 항목 하나의 수명 핸들. Dispose 가 곧 등록 해제이므로 <c>using</c> 으로만 쓴다 —
/// 예외·취소 경로에서 해제가 빠지면 레지스트리에 유령 항목이 남고 그것이 곧 오진의 근원이 된다.
/// </summary>
public interface IAgentActivityScope : IDisposable
{
    Guid Id { get; }

    /// <summary>이 항목 전용 취소 토큰(부모 토큰에 연결). 프로세스 항목은 <see cref="CancellationToken.None"/>.</summary>
    CancellationToken Token { get; }

    /// <summary>턴의 반복 진행을 갱신한다(턴 항목에서만 의미가 있다).</summary>
    void ReportIteration(int iteration);

    /// <summary>
    /// 종료 상태를 명시한다. 호출하지 않으면 Dispose 시점에 토큰 상태로 추정한다
    /// (취소됨 → <see cref="AgentActivityState.Canceled"/>, 아니면 <see cref="AgentActivityState.Completed"/>).
    /// </summary>
    void Complete(AgentActivityState state, string? note = null);
}
