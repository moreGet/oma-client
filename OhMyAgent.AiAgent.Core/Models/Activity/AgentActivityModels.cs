using System;
using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 태스크 매니저가 관리하는 항목의 종류.
///
/// 여기에 "스레드"가 없는 것은 의도다. .NET 에서 관리 스레드를 강제 종료하는 수단은 존재하지 않는다
/// (<c>Thread.Abort</c> 는 .NET Core 에서 제거됐고 호출하면 <see cref="PlatformNotSupportedException"/>).
/// 그래서 관리 단위를 스레드가 아니라 <b>작업(활동)</b> 으로 잡는다 — 작업은 협조적 취소
/// (<see cref="System.Threading.CancellationToken"/>)로 멈추고, 작업이 띄운 <see cref="ChildProcess"/> 만
/// OS 수준에서 강제 종료할 수 있다. UI 문구도 이 구분을 흐리지 말 것("스레드를 죽였다"는 사실이 아니다).
/// </summary>
public enum AgentActivityKind
{
    /// <summary>에이전트 턴 1회(모델 호출 → 도구 실행 반복). 서브에이전트 턴도 같은 종류이며 부모 도구 아래에 붙는다.</summary>
    Turn,

    /// <summary>도구 실행 1건. 오케스트레이터가 최대 8개를 병렬로 돌린다.</summary>
    Tool,

    /// <summary>도구가 띄운 OS 자식 프로세스. 유일하게 "강제 종료"가 실제로 가능한 항목이다.</summary>
    ChildProcess,
}

/// <summary>항목의 수명 상태. 취소 <b>요청</b>과 실제 종료를 구분하는 것이 이 열거형의 핵심이다.</summary>
public enum AgentActivityState
{
    Running,

    /// <summary>취소를 요청했으나 아직 끝나지 않음. 이 상태로 오래 남아 있으면 협조적 취소가 통하지 않는 항목이다.</summary>
    CancelRequested,

    Completed,
    Canceled,
    Failed,
}

/// <summary>
/// 표시 강조용 건강도. 순수 함수(<c>ActivityHealthRules.Classify</c>)로만 계산하며 저장하지 않는다 —
/// 시간이 흐르면 값이 바뀌는 파생값을 상태로 두면 반드시 최신화 누락이 생긴다.
/// </summary>
public enum AgentActivityHealth
{
    Normal,

    /// <summary>같은 종류의 통상 소요를 크게 넘겼다. 멈춘 것처럼 보이는 항목을 사용자가 먼저 보게 한다.</summary>
    LongRunning,

    /// <summary>취소를 요청한 직후(유예 시간 안). 정상적으로 접히는 중일 수 있다.</summary>
    CancelPending,

    /// <summary>취소를 요청했는데 유예 시간을 넘겨도 안 끝났다 — 토큰을 보지 않는 코드에 갇혀 있다.</summary>
    CancelStalled,
}

/// <summary>추적 중인 PID 를 다시 관측했을 때의 판정. <see cref="Recycled"/> 가 이 설계의 안전장치다.</summary>
public enum TrackedProcessVerdict
{
    /// <summary>같은 프로세스가 살아 있다(PID + 시작 시각 + 이름 일치).</summary>
    Alive,

    /// <summary>해당 PID 로 프로세스가 없다 — 우리가 띄운 것이 종료됐다.</summary>
    Exited,

    /// <summary>
    /// PID 는 살아 있지만 우리가 기록한 프로세스가 아니다. OS 는 PID 를 재사용하므로
    /// 이 경우 <b>절대 종료해서는 안 된다</b>(남의 프로세스다). 추적만 버린다.
    /// </summary>
    Recycled,

    /// <summary>동일성을 확인할 수 없다(시작 시각 조회 실패 등). 확신이 없으면 죽이지 않는다.</summary>
    Unverifiable,
}

/// <summary>자식 프로세스 강제 종료 시도의 결과.</summary>
public enum ProcessKillOutcome
{
    Killed,

    /// <summary>이미 종료돼 있었다(할 일 없음 — 실패가 아니다).</summary>
    AlreadyExited,

    /// <summary>PID 가 재사용됐다 — 남의 프로세스이므로 손대지 않았다.</summary>
    IdentityMismatch,

    /// <summary>동일성 확인이 불가능해 보류했다.</summary>
    Unverifiable,

    /// <summary>추적 목록에 없는 항목이다(이미 정리됐거나 잘못된 id).</summary>
    NotTracked,

    /// <summary>종료를 시도했으나 OS 가 거부했다(권한 등).</summary>
    Failed,
}

/// <summary>
/// 우리가 띄운 프로세스의 신원. <b>PID 단독으로는 신원이 되지 않는다</b> — OS 가 PID 를 재사용하기 때문에
/// 시작 시각(+이름)을 함께 기록해야 "그때 그 프로세스"임을 다시 확인할 수 있다.
/// <paramref name="StartedAtUtc"/> 가 null 이면 시작 시각을 읽지 못한 것이고, 그런 항목은 강제 종료 대상에서 제외된다.
/// </summary>
public sealed record TrackedProcessIdentity(int Pid, string ProcessName, DateTimeOffset? StartedAtUtc);

/// <summary>지금 OS 에서 관측한 프로세스 상태. null 반환(=관측 실패)과 구분하려고 별도 형식으로 둔다.</summary>
public sealed record ProcessObservation(int Pid, string ProcessName, DateTimeOffset? StartedAtUtc);

/// <summary>
/// UI 가 읽는 항목 1개. 불변 스냅샷이라 스레드 경계를 그대로 넘길 수 있다(레지스트리 내부 노드는 절대 노출하지 않는다).
/// <paramref name="Depth"/> 는 이미 계산된 들여쓰기 깊이다 — 뷰가 트리를 다시 조립하지 않아도 되게 한다.
/// </summary>
public sealed record AgentActivitySnapshot(
    Guid Id,
    Guid? ParentId,
    AgentActivityKind Kind,
    string Title,
    string? Detail,
    ToolRisk? Risk,
    int? Pid,
    int? Iteration,
    int? MaxIterations,
    DateTimeOffset StartedAtUtc,
    TimeSpan Elapsed,
    TimeSpan? CancelRequestedFor,
    AgentActivityState State,
    AgentActivityHealth Health,
    bool CanCancel,
    bool CanKill,
    bool IsOrphanCandidate,
    int Depth);

/// <summary>
/// 최근에 끝난 항목. 왜 남기는가 — 태스크 매니저가 "진행 중"만 보여 주면 대부분의 시간에 빈 목록이고,
/// 무엇보다 <b>사용자가 누른 중지가 실제로 먹었는지</b> 확인할 방법이 사라진다(누르면 목록에서 사라질 뿐이다).
/// 고정 개수만 보관해 무한 증가를 막는다.
/// </summary>
public sealed record AgentActivityHistoryEntry(
    Guid Id,
    AgentActivityKind Kind,
    string Title,
    string? Detail,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    TimeSpan Duration,
    AgentActivityState FinalState,
    string? Note);

/// <summary>
/// 태스크 매니저 화면 1장. 항목은 <b>깊이 우선 순서로 정렬돼</b> 오므로 위에서 아래로 그리면 계층이 된다.
/// </summary>
public sealed record AgentActivityView(
    IReadOnlyList<AgentActivitySnapshot> Items,
    IReadOnlyList<AgentActivityHistoryEntry> Recent,
    DateTimeOffset CapturedAtUtc)
{
    public static readonly AgentActivityView Empty =
        new(Array.Empty<AgentActivitySnapshot>(), Array.Empty<AgentActivityHistoryEntry>(), default);

    /// <summary>진행 중(취소 요청 포함) 항목 수 — 상단 요약 배지용.</summary>
    public int RunningCount
    {
        get
        {
            var n = 0;
            foreach (var i in Items)
                if (i.State is AgentActivityState.Running or AgentActivityState.CancelRequested) n++;
            return n;
        }
    }

    /// <summary>취소가 통하지 않는 항목 수. 0 이 아니면 사용자에게 "자식 프로세스 강제 종료"를 안내해야 한다.</summary>
    public int CancelStalledCount
    {
        get
        {
            var n = 0;
            foreach (var i in Items)
                if (i.Health == AgentActivityHealth.CancelStalled) n++;
            return n;
        }
    }

    /// <summary>고아 후보 프로세스 수 — "관련 정리" 버튼을 켤지 판단하는 값.</summary>
    public int OrphanCandidateCount
    {
        get
        {
            var n = 0;
            foreach (var i in Items)
                if (i.IsOrphanCandidate) n++;
            return n;
        }
    }
}

/// <summary>
/// 고아 프로세스 정리 결과. 숫자만 주지 않고 <paramref name="Notes"/> 를 함께 주는 이유는
/// "정리했다"보다 <b>무엇을 건드리지 않았는지</b>(PID 재사용·확인 불가·권한 거부)를 보여 주는 게 중요하기 때문이다.
/// </summary>
public sealed record OrphanSweepResult(
    int Inspected,
    int Killed,
    int AlreadyExited,
    int Skipped,
    IReadOnlyList<string> Notes)
{
    public static readonly OrphanSweepResult None = new(0, 0, 0, 0, Array.Empty<string>());
}
