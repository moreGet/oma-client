using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services.Loop;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 병합 갱신(<see cref="ActivityRowSync"/>)이 항목을 짝지을 키. 등기소 스냅샷의 <c>Id</c> 를 그대로 쓴다 —
/// 1초마다 컬렉션을 통째로 갈아 끼우면 스크롤과 마우스 아래 있던 버튼이 매초 튄다.
/// </summary>
public interface IKeyedRow
{
    Guid Id { get; }
}

/// <summary>
/// 태스크 매니저 목록의 한 줄. 등기소 스냅샷(<see cref="AgentActivitySnapshot"/>)의 표시용 투영이며
/// <b>판정을 다시 하지 않는다</b> — <c>CanCancel</c>/<c>CanKill</c> 은 Core 가 계산한 값을 그대로 옮긴다
/// (안전 판정을 UI 가 복제하면 두 곳이 어긋나고, 어긋나는 쪽이 곧 "남의 프로세스를 죽인다"가 된다).
///
/// 프로퍼티를 <c>init</c> 이 아니라 <c>[ObservableProperty]</c> 로 둔 이유는 1초 갱신이
/// <b>같은 인스턴스를 제자리에서 고쳐 쓰기</b> 때문이다(<see cref="Update"/>).
/// </summary>
public sealed partial class ActivityItemViewModel : ObservableObject, IKeyedRow
{
    public Guid Id { get; }

    public ActivityItemViewModel(AgentActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Id = snapshot.Id;
        Update(snapshot);
    }

    /// <summary>턴 / 도구 / 프로세스. 어떤 동작 버튼을 <b>보여줄지</b>를 이 값이 정한다(활성 여부는 CanCancel/CanKill).</summary>
    [ObservableProperty] private AgentActivityKind _kind;

    /// <summary>종류 배지 문구("턴"/"도구"/"프로세스"). 글리프 대신 짧은 한국어를 쓴다(§UI 요약 참조).</summary>
    [ObservableProperty] private string _kindText = "";

    [ObservableProperty] private string _title = "";

    /// <summary>인자 요약(명령 원문·경로 등). 병렬 도구 8줄이 이름만 같을 때 이것이 유일한 식별 수단이다.</summary>
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _hasDetail;

    /// <summary>도구 위험도(색 표시용). 턴·프로세스는 null 이다.</summary>
    [ObservableProperty] private ToolRisk? _risk;
    [ObservableProperty] private bool _hasRisk;

    /// <summary>경과 시간 표기. 새 포매터를 만들지 않고 <see cref="LoopIntervalParser.Format"/> 을 재사용한다.</summary>
    [ObservableProperty] private string _elapsedText = "";

    [ObservableProperty] private string _stateText = "";

    /// <summary>턴의 반복 진행("반복 3/25"). 도구·프로세스는 빈 문자열.</summary>
    [ObservableProperty] private string _iterationText = "";
    [ObservableProperty] private bool _hasIteration;

    /// <summary>사용자에게 지금 무엇을 할 수 있는지 알려주는 한 줄(중지 지연 안내 등).</summary>
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private bool _hasNote;

    /// <summary>강조 색 판정. 저장하지 않고 스냅샷마다 Core 가 다시 계산해 준 값이다.</summary>
    [ObservableProperty] private AgentActivityHealth _health;

    /// <summary>"중지" 버튼 활성 조건 — Core 값 그대로.</summary>
    [ObservableProperty] private bool _canCancel;

    /// <summary>"강제 종료" 버튼 활성 조건 — 신원이 확인된 자식 프로세스만 true.</summary>
    [ObservableProperty] private bool _canKill;

    /// <summary>
    /// "중지" 버튼을 <b>둘지</b> 여부. 자식 프로세스 행에는 두지 않는다 — 프로세스에 협조적 취소는
    /// 존재하지 않고(<c>RequestCancel</c> 이 항상 false), 있는 것처럼 보이면 눌러도 아무 일이 없다.
    /// </summary>
    [ObservableProperty] private bool _supportsCancel;

    /// <summary>"강제 종료" 버튼을 둘지 여부(= 자식 프로세스 행인가).</summary>
    [ObservableProperty] private bool _isProcess;

    /// <summary>소유 작업이 이미 끝난 프로세스 — "관련 프로세스 정리"의 대상.</summary>
    [ObservableProperty] private bool _isOrphanCandidate;

    /// <summary>들여쓰기 깊이. 등기소가 이미 깊이 우선 정렬 + 깊이 계산까지 끝내 주므로 트리를 다시 조립하지 않는다.</summary>
    [ObservableProperty] private int _depth;

    /// <summary>같은 인스턴스를 제자리에서 갱신한다(새 인스턴스를 만들면 스크롤·호버가 매초 튄다).</summary>
    public void Update(AgentActivitySnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);

        Kind        = s.Kind;
        KindText    = TaskManagerText.KindText(s.Kind);
        Title       = s.Title;
        Detail      = s.Detail ?? "";
        HasDetail   = !string.IsNullOrWhiteSpace(s.Detail);
        Risk        = s.Risk;
        HasRisk     = s.Risk is not null;
        ElapsedText = LoopIntervalParser.Format(s.Elapsed);
        StateText   = TaskManagerText.StateText(s.State);

        IterationText = TaskManagerText.IterationText(s.Iteration, s.MaxIterations);
        HasIteration  = IterationText.Length > 0;

        NoteText = TaskManagerText.RowNote(s.Health, s.CancelRequestedFor);
        HasNote  = NoteText.Length > 0;

        Health            = s.Health;
        CanCancel         = s.CanCancel;
        CanKill           = s.CanKill;
        SupportsCancel    = s.Kind is AgentActivityKind.Turn or AgentActivityKind.Tool;
        IsProcess         = s.Kind is AgentActivityKind.ChildProcess;
        IsOrphanCandidate = s.IsOrphanCandidate;
        Depth             = s.Depth;
    }
}

/// <summary>
/// 완료 이력 한 줄. 진행 중 목록만 보여 주면 대부분의 시간에 빈 화면이고, 무엇보다
/// <b>사용자가 누른 중지가 실제로 먹었는지</b> 확인할 방법이 사라진다(누르면 목록에서 사라질 뿐이다).
/// </summary>
public sealed partial class ActivityHistoryItemViewModel : ObservableObject, IKeyedRow
{
    public Guid Id { get; }

    public ActivityHistoryItemViewModel(AgentActivityHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Id = entry.Id;
        Update(entry);
    }

    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _hasDetail;
    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _finishedAtText = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _hasNote;
    [ObservableProperty] private AgentActivityState _finalState;

    public void Update(AgentActivityHistoryEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);

        KindText     = TaskManagerText.KindText(e.Kind);
        Title        = e.Title;
        Detail       = e.Detail ?? "";
        HasDetail    = !string.IsNullOrWhiteSpace(e.Detail);
        DurationText = LoopIntervalParser.Format(e.Duration);
        StateText    = TaskManagerText.StateText(e.FinalState);
        // 이력은 "몇 초 전"이 아니라 시각으로 보여야 한다 — 1초 타이머로 다시 계산할 필요가 없고,
        // 로그·전사와 눈으로 맞춰 볼 수 있다.
        FinishedAtText = e.FinishedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        Note           = e.Note ?? "";
        HasNote        = !string.IsNullOrWhiteSpace(e.Note);
        FinalState     = e.FinalState;
    }
}

/// <summary>
/// <see cref="ObservableCollection{T}"/> 을 스냅샷에 맞춰 <b>Id 기준으로 병합</b>한다.
///
/// 왜 통째로 갈지 않는가: 태스크 매니저는 1초마다 스냅샷을 다시 뜬다. <c>Clear()</c> + 재삽입을 하면
/// 매초 스크롤 위치가 초기화되고 마우스 아래 있던 버튼이 사라져 클릭이 빗나간다(계약이 명시한 위험).
/// 살아 있는 행은 같은 인스턴스를 유지하고 순서만 <see cref="ObservableCollection{T}.Move"/> 로 맞춘다.
///
/// 항목 수가 수십 개 규모라 선형 탐색으로 충분하다(색인을 두면 Move/Insert 마다 무효화돼 오히려 틀리기 쉽다).
/// </summary>
internal static class ActivityRowSync
{
    public static void Sync<TRow, TModel>(
        ObservableCollection<TRow> target,
        IReadOnlyList<TModel>      source,
        Func<TModel, Guid>         keyOf,
        Func<TModel, TRow>         create,
        Action<TRow, TModel>       update)
        where TRow : class, IKeyedRow
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        for (var i = 0; i < source.Count; i++)
        {
            var model = source[i];
            var id    = keyOf(model);

            // 대다수 갱신은 순서까지 그대로다 — 그 경우 비교 한 번으로 끝난다.
            if (i < target.Count && target[i].Id == id)
            {
                update(target[i], model);
                continue;
            }

            var found = IndexOf(target, id, i);
            if (found >= 0)
            {
                target.Move(found, i);
                update(target[i], model);
                continue;
            }

            target.Insert(i, create(model));
        }

        // 남은 꼬리는 사라진 항목이다(뒤에서 지워야 인덱스가 밀리지 않는다).
        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static int IndexOf<TRow>(ObservableCollection<TRow> target, Guid id, int from)
        where TRow : class, IKeyedRow
    {
        for (var i = from; i < target.Count; i++)
            if (target[i].Id == id) return i;
        return -1;
    }
}

/// <summary>
/// 태스크 매니저의 사용자 문구 전부. 순수 함수로 모아 둔 이유는 두 가지다:
///  1. <b>문구 규약을 테스트로 잠글 수 있다</b> — ".NET 에서 관리 스레드 강제 종료는 불가능"하므로
///     "스레드 종료" 류 표현이 하나라도 새어 나가면 사용자에게 거짓을 말하는 것이 된다.
///  2. "안 죽였다"(PID 재사용·확인 불가)를 반드시 사용자에게 알려야 한다 — 숫자만 주면
///     "왜 안 지워졌지"가 남는다.
/// </summary>
internal static class TaskManagerText
{
    /// <summary>종류 배지. 글리프 대신 짧은 한국어 — 세 종류의 의미 차이가 아이콘으로는 전달되지 않는다.</summary>
    public static string KindText(AgentActivityKind kind) => kind switch
    {
        AgentActivityKind.Turn         => "턴",
        AgentActivityKind.Tool         => "도구",
        AgentActivityKind.ChildProcess => "프로세스",
        _                              => "작업",
    };

    /// <summary>
    /// 수명 상태. 취소를 "중지"로 부른다(도구·턴에 쓰는 것은 협조적 취소 요청이다).
    /// "스레드"라는 단어를 쓰지 않는다 — 관리 스레드를 강제 종료하는 수단은 존재하지 않는다.
    /// </summary>
    public static string StateText(AgentActivityState state) => state switch
    {
        AgentActivityState.Running         => "실행 중",
        AgentActivityState.CancelRequested => "중지 요청",
        AgentActivityState.Completed       => "완료",
        AgentActivityState.Canceled        => "중지됨",
        AgentActivityState.Failed          => "실패",
        _                                  => "",
    };

    /// <summary>턴의 반복 진행. 도구·프로세스에는 값이 없어 빈 문자열이 된다.</summary>
    public static string IterationText(int? iteration, int? maxIterations)
    {
        if (iteration is not { } i || i <= 0) return "";
        return maxIterations is { } max && max > 0 ? $"반복 {i}/{max}" : $"반복 {i}";
    }

    /// <summary>
    /// 행 아래 한 줄 안내. <see cref="AgentActivityHealth.CancelStalled"/> 일 때가 핵심이다 —
    /// 그 순간 사용자가 실제로 손쓸 수 있는 유일한 수단이 자식 프로세스 강제 종료라는 사실을 알려야 한다.
    /// </summary>
    public static string RowNote(AgentActivityHealth health, TimeSpan? cancelRequestedFor) => health switch
    {
        AgentActivityHealth.CancelStalled =>
            $"중지를 요청한 뒤 {LoopIntervalParser.Format(cancelRequestedFor ?? TimeSpan.Zero)} 지났지만 아직 끝나지 않았습니다 — "
            + "아래 자식 프로세스가 있으면 강제 종료할 수 있습니다.",
        AgentActivityHealth.CancelPending =>
            $"중지 요청 {LoopIntervalParser.Format(cancelRequestedFor ?? TimeSpan.Zero)} 경과 — 협조적 중지라 즉시 끝나지 않을 수 있습니다.",
        AgentActivityHealth.LongRunning =>
            "같은 종류의 통상 소요를 넘겼습니다.",
        _ => "",
    };

    /// <summary>하단 요약. 0 이면 숫자를 늘어놓지 않고 "없다"고 말한다.</summary>
    public static string Summary(int running, int cancelStalled, int orphanCandidates)
    {
        if (running <= 0 && cancelStalled <= 0 && orphanCandidates <= 0)
            return "진행 중인 작업이 없습니다.";

        var sb = new StringBuilder();
        sb.Append($"진행 중 {running}개");
        if (cancelStalled > 0) sb.Append($" · 중지 지연 {cancelStalled}개");
        if (orphanCandidates > 0) sb.Append($" · 정리 대상 프로세스 {orphanCandidates}개");
        return sb.ToString();
    }

    /// <summary>개별 중지 결과. 눌러도 즉시 사라지지 않는 것이 정상임을 함께 말한다.</summary>
    public static string CancelResult(bool signaled, string title) => signaled
        ? $"\"{title}\" 에 중지를 요청했습니다 — 협조적 중지라 즉시 끝나지 않을 수 있습니다."
        : "이 항목은 중지할 수 없습니다(이미 끝났거나 자식 프로세스입니다).";

    /// <summary>전역 중지 결과. 0 을 "성공"처럼 말하지 않는다.</summary>
    public static string CancelAllResult(int signaled) => signaled <= 0
        ? "중지할 작업이 없습니다."
        : $"{signaled}개 작업에 중지를 요청했습니다 — 협조적 중지라 즉시 끝나지 않을 수 있습니다.";

    /// <summary>
    /// 강제 종료 결과. <see cref="ProcessKillOutcome.IdentityMismatch"/>·
    /// <see cref="ProcessKillOutcome.Unverifiable"/> 는 <b>"안 죽였다"</b>이므로 그 사실을 분명히 말한다 —
    /// 죽은 줄 알고 넘어가면 사용자가 프로세스를 계속 방치한다.
    /// </summary>
    public static string KillOutcome(ProcessKillOutcome outcome, string title) => outcome switch
    {
        ProcessKillOutcome.Killed => $"\"{title}\" 을(를) 강제 종료했습니다.",
        ProcessKillOutcome.AlreadyExited => "이미 종료된 프로세스였습니다 — 추적만 해제했습니다.",
        ProcessKillOutcome.IdentityMismatch =>
            "종료하지 않았습니다 — 그 PID 가 다른 프로세스에 재사용됐습니다(남의 프로세스는 건드리지 않습니다). 추적만 해제했습니다.",
        ProcessKillOutcome.Unverifiable =>
            "종료하지 않았습니다 — 같은 프로세스인지 확인할 수 없습니다(시작 시각을 읽지 못했습니다).",
        ProcessKillOutcome.NotTracked => "추적 목록에 없는 항목입니다 — 이미 정리되었습니다.",
        ProcessKillOutcome.Failed => "종료를 시도했지만 운영체제가 거부했습니다(권한 등).",
        _ => "",
    };

    /// <summary>
    /// 고아 정리 결과. 숫자 뒤에 <see cref="OrphanSweepResult.Notes"/> 를 그대로 붙인다 —
    /// "무엇을 건드리지 않았는지"가 "몇 개 지웠는지"보다 중요하다.
    /// </summary>
    public static string SweepResult(OrphanSweepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Inspected 는 "실제로 종료를 시도한 개수"라서, PID 재사용·확인 불가로 건드리지 않은 항목만
        // 있으면 0 이 된다. 그때 여기서 조기 반환하면 정확히 그 이유(Notes)가 사라진다 —
        // 사용자가 알아야 하는 것은 "왜 안 지워졌는지"이므로, 남길 말이 하나라도 있으면 요약을 낸다.
        if (result.Inspected <= 0 && result.Skipped <= 0 && result.AlreadyExited <= 0 && result.Notes.Count == 0)
            return "정리할 프로세스가 없습니다(우리가 띄웠고 소유 작업이 끝난 것만 대상입니다).";

        var sb = new StringBuilder(
            $"검사 {result.Inspected}개 · 강제 종료 {result.Killed}개 · 이미 종료 {result.AlreadyExited}개 · 건드리지 않음 {result.Skipped}개");

        if (result.Notes.Count > 0)
            sb.Append(" — ").Append(string.Join(" / ", result.Notes));

        return sb.ToString();
    }
}
