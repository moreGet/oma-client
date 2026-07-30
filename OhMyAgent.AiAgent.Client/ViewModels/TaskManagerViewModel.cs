using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Loop;
using Timer = System.Timers.Timer;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 태스크 매니저 창의 DataContext — 실행 중인 턴·도구·자식 프로세스를 보여주고 조작한다.
///
/// 이 VM 이 하지 않는 것 두 가지가 설계의 핵심이다:
///  1. <b>안전 판정을 다시 하지 않는다.</b> "중지할 수 있는가"(<c>CanCancel</c>)와 "강제 종료해도 되는가"
///     (<c>CanKill</c>, PID 재사용·신원 확인 불가 배제)는 Core 의 순수 규칙이 정하며 여기서는 그대로 옮긴다.
///  2. <b>트리를 다시 조립하지 않는다.</b> 등기소 스냅샷은 이미 깊이 우선 정렬 + <c>Depth</c> 계산까지
///     끝나서 오므로, 뷰는 깊이만큼 들여쓰기만 하면 된다.
///
/// 갱신 분업(계약): 구조 변화는 등기소 <c>Changed</c> 이벤트(배경 스레드 발화 → UI 마샬 필수),
/// 경과 시간은 <b>창이 열려 있는 동안만</b> 1초 타이머로 스냅샷 재조회.
/// 경과 갱신을 Core 로 밀지 않는 이유는 (a) 창을 보지 않을 때도 UI 스레드를 깨우고
/// (b) 헤드리스가 쓰지도 않는 타이머를 돌리기 때문이다 — 표시 주기는 표시 계층의 책임이다.
///
/// 문구 규약: 도구·턴은 <b>"중지"</b>(협조적 취소), 자식 프로세스만 <b>"강제 종료"</b>.
/// "스레드 종료" 류 표현은 쓰지 않는다 — .NET 에서 관리 스레드 강제 종료는 불가능하고,
/// 사용자에게 그렇게 했다고 오인시켜서는 안 된다. 문구는 전부 <c>TaskManagerText</c> 에 모여 테스트로 잠겨 있다.
/// </summary>
public sealed partial class TaskManagerViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 경과 시간 표시 주기. 1초보다 짧게 잡을 이유가 없다(표기 단위가 초다) —
    /// <c>Snapshot()</c> 이 프로세스 항목마다 OS 관측을 1회 하므로 주기를 줄이면 그만큼 syscall 이 늘어난다.
    /// </summary>
    private const double RefreshIntervalMs = 1000;

    private readonly IAgentActivityRegistry _activities;
    private readonly ILoopController        _loop;
    private readonly IUiDispatcher          _ui;
    private readonly IDialogService         _dialogs;
    private readonly Timer                  _timer;

    private bool _attached;
    private bool _disposed;

    /// <summary>
    /// 창을 닫아 달라는 요청. 창은 View 소유라 VM 이 직접 닫을 수 없다 —
    /// <c>TaskManagerCoordinator</c> 가 단독으로 구독한다(창이 구독하면 인스턴스마다 핸들러가 쌓인다).
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// <paramref name="dialogs"/> 를 받는 이유: 전역 중지·관련 프로세스 정리·강제 종료 셋은 되돌릴 수 없어
    /// 확인을 거쳐야 한다(개별 중지는 협조적이라 즉시 실행이 낫다). VM 이 <c>MessageBox</c> 를 직접
    /// 부르지 않게 서비스로 받는다.
    /// </summary>
    public TaskManagerViewModel(
        IAgentActivityRegistry activities,
        ILoopController        loop,
        IUiDispatcher          ui,
        IDialogService         dialogs)
    {
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _loop       = loop       ?? throw new ArgumentNullException(nameof(loop));
        _ui         = ui         ?? throw new ArgumentNullException(nameof(ui));
        _dialogs    = dialogs    ?? throw new ArgumentNullException(nameof(dialogs));

        // AutoReset — 매초 다시 뜬다. 콜백은 스레드 풀에서 오므로 반드시 UI 로 마샬한다.
        _timer = new Timer(RefreshIntervalMs) { AutoReset = true };
        _timer.Elapsed += OnTick;
    }

    /// <summary>진행 중 항목(깊이 우선 순서). 통째로 갈지 않고 <c>Id</c> 기준 병합 갱신한다.</summary>
    public ObservableCollection<ActivityItemViewModel> Items { get; } = [];

    /// <summary>최근 완료 30개(최신 위). "내가 누른 중지가 실제로 먹었는지" 확인하는 유일한 창구다.</summary>
    public ObservableCollection<ActivityHistoryItemViewModel> Recent { get; } = [];

    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private int    _runningCount;
    [ObservableProperty] private int    _cancelStalledCount;
    [ObservableProperty] private int    _orphanCandidateCount;

    /// <summary>목록이 비었을 때 "없다"고 말해 주기 위한 플래그(빈 표를 보여 주면 고장처럼 읽힌다).</summary>
    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private bool _hasRecent;

    /// <summary>중지 지연 항목이 하나라도 있으면 상단에 안내 배너를 띄운다.</summary>
    [ObservableProperty] private bool _hasCancelStalled;

    /// <summary>
    /// <c>/loop</c> 행. 문구는 <see cref="LoopStatusFormatter.Describe"/> 재사용 — GUI 와 CLI 가
    /// 다른 말을 하면 그것 자체가 버그 리포트가 된다. 미실행이면 빈 문자열(= 행을 감춘다).
    /// </summary>
    [ObservableProperty] private string _loopStatusText = "";

    /// <summary>마지막 조작의 결과 한 줄. 강제 종료가 "안 죽였다"로 끝난 경우를 여기서 알린다.</summary>
    [ObservableProperty] private string _actionResultText = "";

    /// <summary>마지막 갱신 시각("갱신 14:03:21") — 타이머가 죽었는지 눈으로 확인할 수 있어야 한다.</summary>
    [ObservableProperty] private string _lastRefreshText = "";

    // ── 수명 (코디네이터가 창 수명에 맞춰 호출) ──────────────────────────

    /// <summary>
    /// 창이 열릴 때. 등기소·루프 구독을 걸고 1초 타이머를 돌린다.
    /// 멱등이다 — 창을 연타해도 구독이 겹치지 않는다.
    /// </summary>
    internal void Attach()
    {
        if (_disposed || _attached) return;
        _attached = true;

        _activities.Changed += OnActivitiesChanged;
        _loop.LoopChanged   += OnLoopChanged;

        Refresh();          // 창이 뜬 첫 프레임이 비어 보이지 않게 즉시 1회
        _timer.Start();
    }

    /// <summary>
    /// 창이 닫힐 때. <b>반드시</b> 타이머를 멈추고 구독을 해제한다 —
    /// 등기소·루프 컨트롤러는 App 수명 싱글턴이라 창마다 구독이 쌓이고,
    /// 보지도 않는 화면 때문에 매초 프로세스 관측 syscall 이 계속 돈다.
    /// </summary>
    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;

        _timer.Stop();
        _activities.Changed -= OnActivitiesChanged;
        _loop.LoopChanged   -= OnLoopChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Detach();
        _timer.Elapsed -= OnTick;
        _timer.Dispose();
    }

    // 등기소·루프 이벤트는 임의의 배경 스레드에서 온다(TodoService/LoopController 관례) → UI 마샬 필수.
    private void OnActivitiesChanged(object? sender, EventArgs e) => _ = _ui.InvokeAsync(Refresh);

    private void OnLoopChanged(object? sender, LoopEvent e) => _ = _ui.InvokeAsync(Refresh);

    private void OnTick(object? sender, System.Timers.ElapsedEventArgs e) => _ = _ui.InvokeAsync(Refresh);

    // ── 갱신 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 스냅샷 1장을 화면에 반영한다. "새로고침" 버튼도 이 커맨드를 쓴다.
    ///
    /// <c>Snapshot()</c> 에는 의도된 부수효과가 하나 있다 — 이미 종료된 프로세스 항목을 걷어내고 이력에 남긴다.
    /// 1초마다 부르는 것이 정상 사용법이다(그래서 여기서 <c>Changed</c> 가 재발화하지 않는다 = 무한 왕복 없음).
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        var view = _activities.Snapshot();

        // 통째로 갈아 끼우지 않는다 — 매초 스크롤이 초기화되고 마우스 아래 버튼이 사라진다.
        ActivityRowSync.Sync(
            Items, view.Items,
            s => s.Id,
            s => new ActivityItemViewModel(s),
            (row, s) => row.Update(s));

        ActivityRowSync.Sync(
            Recent, view.Recent,
            h => h.Id,
            h => new ActivityHistoryItemViewModel(h),
            (row, h) => row.Update(h));

        RunningCount         = view.RunningCount;
        CancelStalledCount   = view.CancelStalledCount;
        OrphanCandidateCount = view.OrphanCandidateCount;

        HasItems         = Items.Count  > 0;
        HasRecent        = Recent.Count > 0;
        HasCancelStalled = CancelStalledCount > 0;
        SummaryText      = TaskManagerText.Summary(RunningCount, CancelStalledCount, OrphanCandidateCount);

        LoopStatusText  = LoopStatusFormatter.Describe(_loop.Status);
        LastRefreshText = $"갱신 {view.CapturedAtUtc.ToLocalTime():HH:mm:ss}";

        // 버튼 활성은 카운트에서 파생된다 — 커맨드에 알려 줘야 회색으로 바뀐다.
        CancelAllCommand.NotifyCanExecuteChanged();
        SweepOrphansCommand.NotifyCanExecuteChanged();
        StopLoopCommand.NotifyCanExecuteChanged();
    }

    // ── 조작 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 개별 "중지" — 협조적 취소 요청. 확인 대화상자를 두지 않는다: 되돌릴 수 있는 성격이고
    /// (모델은 실패한 도구로 인식해 다음 판단을 이어간다) 확인을 끼우면 급할 때 손이 늦는다.
    /// 눌러도 즉시 사라지지 않는 것이 정상이므로 결과 문구로 그 사실을 남긴다.
    /// </summary>
    [RelayCommand]
    private void CancelItem(ActivityItemViewModel? row)
    {
        if (row is null) return;

        var signaled = _activities.RequestCancel(row.Id);
        ActionResultText = TaskManagerText.CancelResult(signaled, row.Title);
        Refresh();
    }

    /// <summary>
    /// 자식 프로세스 "강제 종료". 되돌릴 수 없어 확인을 거친다.
    /// 등기소가 신원을 재확인한 뒤에만 실제로 죽이므로, PID 재사용·확인 불가로 <b>죽이지 않은</b> 경우가 있다 —
    /// 그때는 결과 문구가 "종료하지 않았습니다"라고 분명히 말한다(죽은 줄 알고 방치하면 안 된다).
    /// </summary>
    [RelayCommand]
    private void KillItem(ActivityItemViewModel? row)
    {
        if (row is null) return;

        if (!_dialogs.Confirm(
                "자식 프로세스 강제 종료",
                $"{row.Title}\n\n이 프로세스를 강제 종료합니다. 저장하지 않은 작업은 사라집니다.\n계속할까요?"))
            return;

        var outcome = _activities.KillChildProcess(row.Id);
        ActionResultText = TaskManagerText.KillOutcome(outcome, row.Title);
        Refresh();
    }

    /// <summary>
    /// "전역 중지" — 진행 중인 모든 턴·도구에 중지를 요청한다.
    /// <b>프로세스는 죽이지 않는다</b>(별개 조작이다) — 그 사실을 확인 문구에 적어 오해를 막는다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelAll))]
    private void CancelAll()
    {
        if (!_dialogs.Confirm(
                "전역 중지",
                $"진행 중인 작업 {RunningCount}개에 중지를 요청합니다.\n"
                + "자식 프로세스는 함께 종료되지 않습니다(각 행의 \"강제 종료\"가 별개 조작입니다).\n계속할까요?"))
            return;

        var signaled = _activities.CancelAll();
        ActionResultText = TaskManagerText.CancelAllResult(signaled);
        Refresh();
    }

    private bool CanCancelAll() => RunningCount > 0;

    /// <summary>
    /// "관련 프로세스 정리" — <b>우리가 띄웠고</b> 소유 작업이 이미 끝났고 아직 우리 것으로 살아 있는
    /// 프로세스만 대상이다(시스템 프로세스 목록을 훑지 않는다). PID 가 재사용된 항목은 건드리지 않는다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSweepOrphans))]
    private void SweepOrphans()
    {
        if (!_dialogs.Confirm(
                "관련 프로세스 정리",
                $"소유 작업이 끝난 프로세스 {OrphanCandidateCount}개를 강제 종료합니다.\n"
                + "이 앱이 띄운 프로세스만 대상이며, PID 가 재사용된 항목은 건드리지 않습니다.\n계속할까요?"))
            return;

        var result = _activities.SweepOrphans();
        ActionResultText = TaskManagerText.SweepResult(result);
        Refresh();
    }

    private bool CanSweepOrphans() => OrphanCandidateCount > 0;

    /// <summary>
    /// <c>/loop</c> 중지. 등기소에 복제하지 않고 컨트롤러를 직접 부른다 —
    /// 루프 상태의 진실 원천은 <see cref="ILoopController"/> 하나다(둘로 나누면 반드시 어긋난다).
    /// 멱등이라 연타해도 안전하다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopLoop))]
    private void StopLoop()
    {
        _loop.Stop(LoopStopReason.UserStopped);
        ActionResultText = "반복 실행에 중지를 요청했습니다.";
        Refresh();
    }

    private bool CanStopLoop() => _loop.IsRunning;

    /// <summary>Esc · 타이틀바 닫기 버튼 — 창만 닫는다(진행 중 작업에는 손대지 않는다).</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
