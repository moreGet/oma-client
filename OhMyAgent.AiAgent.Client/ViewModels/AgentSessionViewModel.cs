using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Loop;
using OhMyAgent.AiAgent.Client.ViewModels.Transcript;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// Root ViewModel driving the autonomous agent loop. Replaces the retired
/// <c>MainViewModel</c>. Consumes <see cref="IAgentOrchestrator"/> and projects
/// its <c>AgentEvent</c> stream onto a transcript of <see cref="ITranscriptItem"/>s,
/// marshalling all UI mutations onto the WPF dispatcher.
/// </summary>
public sealed partial class AgentSessionViewModel : ObservableObject, IDisposable
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAgentApiClient _api;
    private readonly IPermissionService _permissions;
    private readonly IWorkspaceContext _workspace;
    private readonly ISettingsService _settings;
    private readonly IChatHistoryService _chatHistory;
    private readonly IFileAttachmentService _attachmentService;
    private readonly ISuggestionService _suggestions;
    private readonly IToolPolicyService _policy;
    private readonly ISessionSyncService _sessionSync;
    private readonly ITodoService _todos;
    private readonly ILoopController _loop;

    private AgentSession _session = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// /loop 수명 전체를 덮는 취소원(개별 턴의 <see cref="_cts"/> 와 다르다).
    /// 로그아웃·새 대화·Dispose 가 이걸 끊어야 컨트롤러의 대기(Delay)까지 함께 접힌다.
    /// </summary>
    private CancellationTokenSource? _loopCts;

    /// <summary>이번 턴에서 관측한 서버 오류(AgentError). 루프의 연속 실패 판정 근거 — 턴 시작 시 비운다.</summary>
    private string? _turnErrorMessage;

    // 앱 수명 싱글턴 서비스 이벤트 구독 해제 경로(Dispose에서 호출). 형제 Chat VM들과 동일 관례.
    private readonly Action _unsubscribe;

    // Fast lookup from tool CallId -> its transcript card.
    private readonly Dictionary<string, ToolCallViewModel> _toolCards = new();

    // Current open streaming assistant turn (null between turns).
    private AssistantTurnViewModel? _currentAssistant;
    private ThinkingViewModel? _currentThinking;

    // Guards re-entrant settings writes when seeding properties from settings.
    private bool _suppressPersist;

    // ── Observable state ───────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadChatSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RequestAttachFileCommand))]
    [NotifyPropertyChangedFor(nameof(ShowStopButton))]
    [NotifyPropertyChangedFor(nameof(ShowSendButton))]
    private bool _isBusy;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _needsLogin;
    [ObservableProperty] private string _errorTitle = "서버 연결 실패";
    [ObservableProperty] private string _primaryActionText = "다시 시도";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusText = "연결 중...";
    [ObservableProperty] private string _workspaceRoot = string.Empty;

    /// <summary>주 루트 외 추가 활성 작업 디렉토리 표시. 활성이 2개 이상일 때 "외 N개"(N=활성-1), 아니면 빈 문자열.</summary>
    [ObservableProperty] private string _extraWorkspacesText = string.Empty;
    [ObservableProperty] private PermissionMode _currentPermissionMode = PermissionMode.Manual;
    [ObservableProperty] private double _windowOpacity = 1.0;
    [ObservableProperty] private ApprovalRequestViewModel? _pendingApproval;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageDisplayText))]
    private string _lastUsageText = string.Empty;

    // ── Token usage display toggle (Feature 7) ─────────────────────────

    /// <summary>세션 누적 총 토큰량(in+out). ObservableProperty 아님 — 표시 갱신은 UsageDisplayText로 발화.</summary>
    private long _totalTokens;

    /// <summary>true면 상단 토큰 표시를 세션 누적 총량으로, false면 마지막 응답 in/out으로.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageDisplayText))]
    private bool _showTotalUsage;

    /// <summary>상단 토큰 표시 — 기본은 마지막 응답 in/out, 클릭(ShowTotalUsage) 시 세션 누적 총량(천 단위).</summary>
    public string UsageDisplayText => ShowTotalUsage ? $"총 {_totalTokens:N0} 토큰" : LastUsageText;

    /// <summary>
    /// 이번 턴의 에이전트 루프 반복 횟수 — "반복 3/100". 실행 중에만 값이 있고 턴이 끝나면 비운다.
    ///
    /// StatusText 에 섞어 쓰지 않는다: StatusText 는 서버 연결 표시로 고정돼 있어(연결됨/연결 안됨),
    /// 거기에 진행 상태를 덮어쓰면 연결 표시가 사라진다.
    /// </summary>
    [ObservableProperty] private string _iterationDisplayText = string.Empty;

    // ── /loop 반복 실행 상태 ───────────────────────────────────────────

    /// <summary>
    /// /loop 로 시작한 반복 실행이 살아 있는지. 대기(다음 실행까지 카운트다운) 중에도 true 다 —
    /// 그래서 중지 버튼 표시는 <see cref="IsBusy"/> 가 아니라 <see cref="ShowStopButton"/> 을 봐야 한다.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(ShowStopButton))]
    private bool _isLoopRunning;

    /// <summary>
    /// 상단바 루프 표기 — "루프 실행 중 · 반복 3/50 · 다음 실행까지 2분 14초". 미실행 시 빈 문자열(숨김).
    ///
    /// <see cref="IterationDisplayText"/> 와 혼동 금지: 그쪽은 <b>한 턴 안의</b> 오케스트레이터 반복,
    /// 이쪽은 <b>턴 자체를</b> 몇 번째 반복하는지다. 둘은 동시에 값을 가질 수 있어 나란히 표시한다.
    /// </summary>
    [ObservableProperty] private string _loopStatusText = string.Empty;

    /// <summary>컴포저 "에이전트" 서랍(Popup) 개폐. ToggleButton.IsChecked 와 TwoWay 로 묶인다.</summary>
    [ObservableProperty] private bool _isAgentDrawerOpen;

    /// <summary>
    /// 중지 버튼 노출 조건. 루프 대기 중에는 턴이 안 돌아 <see cref="IsBusy"/> 가 false 라,
    /// IsBusy 만 보면 "중지할 수단이 화면에서 사라지는" 상태가 생긴다.
    /// </summary>
    public bool ShowStopButton => IsBusy || IsLoopRunning;

    /// <summary>
    /// 전송 버튼 노출 조건. <see cref="ShowStopButton"/> 의 반대가 <b>아니다</b> —
    /// 루프 대기 중(IsLoopRunning &amp;&amp; !IsBusy)에는 전송과 중지가 <b>함께</b> 보여야 한다.
    /// <see cref="CanSend"/> 가 루프 중에도 슬래시 커맨드를 허용하는데(A2) 버튼이 없으면
    /// "/loop stop" 을 Enter 로만 보낼 수 있게 되어, 마우스 사용자는 루프를 멈출 길이 사라진다.
    /// 턴이 실제로 도는 동안(IsBusy)에만 전송을 감춘다.
    /// </summary>
    public bool ShowSendButton => !IsBusy;

    /// <summary>상단 토큰 표시를 in/out ↔ 세션 누적 총량으로 토글(텍스트 클릭이 바인딩).</summary>
    [RelayCommand]
    private void ToggleUsageView() => ShowTotalUsage = !ShowTotalUsage;

    // ── Phase D observable state ───────────────────────────────────────

    /// <summary>F — greeting heading: "{name}님 안녕하세요, 어떤 업무를 시작할까요?".</summary>
    [ObservableProperty] private string _greetingText = string.Empty;

    /// <summary>F — raw display name (settings UserDisplayName ?? Environment.UserName).</summary>
    [ObservableProperty] private string _userDisplayName = string.Empty;

    /// <summary>작성기 하단 모델 칩에 표시할 모델명. 미설정 시 "미설정".</summary>
    [ObservableProperty] private string _modelDisplayText = "미설정";

    /// <summary>모델 칩 툴팁 — 잘려 표시될 수 있는 전체 모델 ID 를 보여준다.</summary>
    [ObservableProperty] private string _modelTooltipText = string.Empty;

    /// <summary>D — convenience flag mirroring <c>Attachments.Count &gt; 0</c>.</summary>
    [ObservableProperty] private bool _hasAttachments;

    /// <summary>작업 계획 카드 표시 여부(<c>Todos.Count &gt; 0</c>).</summary>
    [ObservableProperty] private bool _hasTodos;

    // ── Version / update notice ────────────────────────────────────────

    /// <summary>업데이트 알림 배너 문구. 비어 있으면 배너 숨김(StringToVisibility).</summary>
    [ObservableProperty] private string _updateNotice = string.Empty;

    /// <summary>필수 업데이트 여부. true면 배너를 경고색으로 표시.</summary>
    [ObservableProperty] private bool _updateMandatory;

    // ── Token quota ────────────────────────────────────────────────────

    /// <summary>쿼터 목록이 있을 때만 칩/팝업을 표시(서버 미응답 → false → 전부 숨김).</summary>
    [ObservableProperty] private bool _hasQuota;

    /// <summary>상단바 칩 요약 — 가장 빡빡한(비무제한 중 잔여율 최소) 윈도우 기준 "{라벨} {잔여율}%", 전부 무제한이면 "무제한".</summary>
    [ObservableProperty] private string _quotaSummary = string.Empty;

    /// <summary>가장 빡빡한 윈도우가 거의 소진(IsConstrained)이면 true → 칩 경고색.</summary>
    [ObservableProperty] private bool _quotaConstrained;

    /// <summary>상단 쿼터 칩이 표시할 윈도우 선택. "auto"(가장 빡빡)|"day"|"week"|"month". 설정에 영속.</summary>
    [ObservableProperty] private string _quotaChipWindow = "auto";

    /// <summary>쿼터 칩 표시 기준 선택지(View 콤보 바인딩). 한글 라벨 매핑은 XAML이 담당.</summary>
    public IReadOnlyList<string> QuotaChipWindowOptions { get; } = ["auto", "day", "week", "month"];

    // ── Sidebar ────────────────────────────────────────────────────────

    /// <summary>좌측 사이드바 접힘 상태. ToggleSidebarCommand로 토글, 설정에 영속(세션 간 유지).</summary>
    [ObservableProperty] private bool _isSidebarCollapsed;

    // ── UI scale (accessibility) ───────────────────────────────────────

    /// <summary>UI 전체 배율(접근성). 메인창 루트 ScaleTransform이 바인딩. 변경은 설정창에서만 → SettingsChanged로 반영.</summary>
    [ObservableProperty] private double _uiScale = 1.0;

    // ── Collections ────────────────────────────────────────────────────

    public ObservableCollection<ITranscriptItem> Transcript { get; } = [];

    /// <summary>에이전트 작업 계획(manage_todos). 항목이 있으면 메인에 계획 카드를 띄운다.</summary>
    public ObservableCollection<TodoItem> Todos { get; } = [];

    /// <summary>C — saved chat session summaries shown in the sidebar "채팅" list.</summary>
    public ObservableCollection<ChatSessionSummary> ChatSessions { get; } = [];

    /// <summary>D — attachment chips bound by the composer.</summary>
    public ObservableCollection<Attachment> Attachments { get; } = [];

    /// <summary>G — welcome-screen action hints (empty until the suggestion service is wired).</summary>
    public ObservableCollection<Suggestion> Suggestions { get; } = [];

    /// <summary>토큰 쿼터 윈도우(일/주/월 순, 항상 3개). 쿼터 팝업이 게이지로 바인딩한다.</summary>
    public ObservableCollection<QuotaWindowViewModel> QuotaWindows { get; } = [];

    /// <summary>Selectable permission modes for the header selector.</summary>
    public IReadOnlyList<PermissionMode> PermissionModes { get; } =
        [PermissionMode.Manual, PermissionMode.AutoSafe, PermissionMode.FullAuto];

    /// <summary>
    /// 프로젝트(대화 컨테이너) 사이드바 VM. App.xaml.cs가 조립 후 주입한다(없을 수 있음).
    /// UIDesigner는 메인 DataContext에서 <c>Projects.*</c>로 바인딩한다.
    /// </summary>
    public ProjectsViewModel? Projects { get; set; }

    // ── Constructor ────────────────────────────────────────────────────

    public AgentSessionViewModel(
        IAgentOrchestrator orchestrator,
        IAgentApiClient api,
        IPermissionService permissions,
        IWorkspaceContext workspace,
        ISettingsService settings,
        IChatHistoryService chatHistory,
        IFileAttachmentService attachments,
        ISuggestionService suggestions,
        IToolPolicyService policy,
        ISessionSyncService sessionSync,
        ITodoService todos,
        ILoopController loop)
    {
        _orchestrator = orchestrator;
        _api = api;
        _permissions = permissions;
        _workspace = workspace;
        _settings = settings;
        _chatHistory = chatHistory;
        _attachmentService = attachments;
        _suggestions = suggestions;
        _policy = policy;
        _sessionSync = sessionSync;
        _todos = todos;
        _loop = loop;
        FileMention = new FileMentionViewModel(workspace);

        // 도구(manage_todos)가 백그라운드 스레드에서 갱신 → UI 디스패처로 마샬해 Todos 컬렉션 재구성.
        _todos.TodosChanged += OnTodosChanged;

        // 루프 이벤트도 백그라운드 스레드 발화 — 상단바/전사 반영은 전부 디스패처를 거친다.
        _loop.LoopChanged += OnLoopChanged;

        // 세션 경계(새 대화/전환/복원/로그아웃)는 모두 Transcript.Clear()를 부르므로, 그때 작업 계획 카드도 리셋.
        Transcript.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) _todos.Clear();
        };

        // Surface Manual-mode approvals through the inline approval card.
        _permissions.SetApprovalHandler(RequestApprovalAsync);

        // #3 — 설정(작업 디렉토리 추가/토글/제거)이 바뀌면 메인 칩·인사말을 즉시 갱신.
        //       SettingsService가 UI 디스패처로 발화하므로 핸들러는 UI 스레드에서 실행된다.
        _settings.SettingsChanged += OnSettingsChanged;

        // D — mirror attachment count onto HasAttachments.
        Attachments.CollectionChanged += (_, _) => HasAttachments = Attachments.Count > 0;

        // 앱 수명 싱글턴(_todos/_settings) 이벤트만 해제 대상 — VM보다 오래 살아 누수를 만든다.
        // Transcript/Attachments.CollectionChanged 는 VM 소유 컬렉션(수명 동일)이라 제외.
        // _permissions.SetApprovalHandler 도 싱글턴↔싱글턴(App 수명 동일)이며 clear API가 없어 제외.
        _unsubscribe = () =>
        {
            _todos.TodosChanged -= OnTodosChanged;
            _settings.SettingsChanged -= OnSettingsChanged;
            _loop.LoopChanged -= OnLoopChanged;
        };

        SeedFromSettings();
    }

    // ── Property change reactions ──────────────────────────────────────

    partial void OnWindowOpacityChanged(double value)
    {
        if (_suppressPersist) return;
        _ = _settings.UpdateOpacityAsync(Math.Clamp(value, 0.3, 1.0));
    }

    partial void OnCurrentPermissionModeChanged(PermissionMode value)
    {
        if (_suppressPersist) return;
        _ = _settings.UpdatePermissionModeAsync(value);
    }

    partial void OnQuotaChipWindowChanged(string value)
    {
        if (_suppressPersist) return;
        _ = _settings.UpdateQuotaChipWindowAsync(value);
        // 목록 재조회 없이 이미 채워진 QuotaWindows 기준으로 칩만 갱신.
        UpdateQuotaSummary();
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        if (_suppressPersist) return;
        _ = _settings.UpdateSidebarCollapsedAsync(value);
    }

    /// <summary>좌측 사이드바를 접거나 편다(상단바 햄버거 버튼이 바인딩).</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    // ── Initialization ─────────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, AppSettings e) => SeedFromSettings();

    private void SeedFromSettings()
    {
        _suppressPersist = true;
        try
        {
            var s = _settings.Current;

            // #3 — 활성(Enabled) 작업 디렉토리 기준으로 주 루트와 "외 N개" 표시를 계산.
            var activePaths = (s.Workspaces ?? [])
                .Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Path))
                .Select(w => w.Path)
                .ToList();

            var primary = activePaths.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(primary))
                primary = string.IsNullOrWhiteSpace(s.WorkspaceRoot) ? _workspace.Root : s.WorkspaceRoot;

            WorkspaceRoot = primary;
            var extra = activePaths.Count - 1;
            ExtraWorkspacesText = extra >= 1 ? $"외 {extra}개" : string.Empty;

            CurrentPermissionMode = s.PermissionMode;
            WindowOpacity = s.Opacity;

            // 상단바/사이드바/접근성 — 설정에서 복원(역기록 가드 구간).
            QuotaChipWindow = s.QuotaChipWindow;
            IsSidebarCollapsed = s.SidebarCollapsed;
            UiScale = s.UiScale;

            // F — greeting from settings display name, falling back to the OS user name.
            var rawName = string.IsNullOrWhiteSpace(s.UserDisplayName) ? Environment.UserName : s.UserDisplayName;
            UserDisplayName = rawName;
            GreetingText = $"{rawName}님 안녕하세요, 어떤 업무를 시작할까요?";

            // 모델 칩 — 설정에서 고른 모델을 그대로 표시. SettingsChanged 로 재진입하므로
            // 설정 저장 즉시 반영된다.
            var modelId = s.ModelId;
            var hasModel = !string.IsNullOrWhiteSpace(modelId);
            ModelDisplayText = hasModel ? modelId : "미설정";
            ModelTooltipText = hasModel
                ? $"모델: {modelId}\n설정 → 서버 설정에서 변경합니다."
                : "모델이 설정되지 않았습니다.\n설정 → 서버 설정에서 선택하세요.";
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    public async Task InitializeAsync()
    {
        SeedFromSettings();
        await RetryConnectionAsync();
        if (IsConnected && !NeedsLogin)
        {
            Transcript.Add(new SystemNoticeViewModel
            {
                Text = "에이전트 서버에 연결되었습니다. 무엇을 도와드릴까요?",
            });

            // 로그인 직후 1회 — 서버 도구 정책(모드+목록) 로드.
            // 서버 미구현(404)이면 조용히 fail-open. 그 외 조회 실패는 fail-closed(전 도구 차단)이므로
            // 삼키면 안 된다 — 도구가 왜 안 듣는지 모를 사용자에게 이유를 보여준다.
            try
            {
                await _policy.LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Transcript.Add(new SystemNoticeViewModel
                {
                    Text = $"⚠ {ex.Message}",
                });
            }

            // 로그인 직후 1회 — 서버 추가 위험명령 패턴 로드(디폴트에 더해짐). 미구현/오류면 디폴트만.
            try { SecurityValidator.SetServerPatterns(await _api.GetCommandSecurityPolicyAsync().ConfigureAwait(true)); }
            catch { /* graceful — 클라 내장 디폴트 블랙리스트가 바닥을 방어한다. */ }

            // 연결 성공 후 best-effort 버전 점검(서버 미구현이면 조용히 무시).
            _ = CheckVersionAsync();

            // 연결+인증 직후 best-effort 쿼터 로드(서버 미응답이면 조용히 숨김).
            _ = RefreshQuotaAsync();

            // 연결+인증 직후 best-effort 세션 동기화 — 다른 PC에서 만든/수정한 대화를 로컬로 끌어온다.
            // RefreshChatSessionsAsync 앞에 배치해 병합분이 사이드바에 즉시 보이도록 한다.
            try { await _sessionSync.PullMergeAsync().ConfigureAwait(true); } catch { /* graceful */ }
        }

        // C — populate the sidebar chat list + project groups from local history.
        await RefreshChatSessionsAsync();

        // G — fetch action hints (currently a stubbed empty list).
        _ = LoadSuggestionsAsync();
    }

    /// <summary>
    /// 서버 버전 정책을 best-effort로 점검해 업데이트 알림을 노출한다.
    /// 서버 미구현/오프라인이면 조용히 종료(하드 블록 없음 — 알림만).
    /// </summary>
    private async Task CheckVersionAsync()
    {
        ClientVersionInfo? info;
        try
        {
            info = await _api.GetClientVersionAsync().ConfigureAwait(false);
        }
        catch
        {
            return;   // graceful — 점검 실패가 앱 동작을 막지 않는다.
        }

        if (info is null) return;

        var current = ParseVersion(AppVersion.Semantic);
        var minimum = ParseVersion(info.MinimumSupported);
        var latest  = ParseVersion(info.Latest);

        var download = string.IsNullOrWhiteSpace(info.DownloadUrl)
            ? string.Empty
            : $" 다운로드: {info.DownloadUrl}";
        var notice = string.IsNullOrWhiteSpace(info.Notice) ? string.Empty : $" {info.Notice}";

        string banner;
        bool mandatory;

        if (current is not null && minimum is not null && current < minimum)
        {
            mandatory = true;
            banner = $"필수 업데이트 필요: 최소 지원 버전 {info.MinimumSupported} (현재 {AppVersion.Semantic}).{notice}{download}";
        }
        else if (current is not null && latest is not null && current < latest)
        {
            mandatory = false;
            banner = $"새 버전 {info.Latest} 사용 가능. (현재 {AppVersion.Semantic}){notice}{download}";
        }
        else
        {
            mandatory = false;
            banner = string.Empty;   // 최신 또는 비교 불가 → 배너 숨김.
        }

        await UiInvokeAsync(() =>
        {
            UpdateMandatory = mandatory;
            UpdateNotice = banner;
        }).ConfigureAwait(false);
    }

    /// <summary>SemVer 문자열을 <see cref="Version"/>으로 파싱. 실패 시 null(비교 생략).</summary>
    private static Version? ParseVersion(string? value)
        => Version.TryParse(value, out var v) ? v : null;

    /// <summary>
    /// 토큰 쿼터를 best-effort로 새로고침한다. 서버 미응답/null(401/500/오프라인)이면
    /// 조용히 쿼터 UI를 숨긴다(HasQuota=false). 있으면 day→week→month 순으로 목록을
    /// 재구성하고 칩 요약/경고 플래그를 갱신한다. 모든 UI 변경은 디스패처로 마샬링.
    /// 쿼터 팝업의 새로고침 버튼이 <c>RefreshQuotaCommand</c>로 바인딩한다.
    /// </summary>
    [RelayCommand]
    private async Task RefreshQuotaAsync()
    {
        QuotaResponse? quota;
        try
        {
            quota = await _api.GetQuotaAsync().ConfigureAwait(false);
        }
        catch
        {
            quota = null;   // graceful — 쿼터 조회 실패가 앱 동작을 막지 않는다.
        }

        if (quota?.Windows is not { Count: > 0 } windows)
        {
            await UiInvokeAsync(() =>
            {
                HasQuota = false;
                QuotaWindows.Clear();
                QuotaSummary = string.Empty;
                QuotaConstrained = false;
            }).ConfigureAwait(false);
            return;
        }

        // 서버 순서(day→week→month) 유지하며 항목 VM 구성.
        var items = windows.Select(w => new QuotaWindowViewModel(w)).ToList();

        await UiInvokeAsync(() =>
        {
            QuotaWindows.Clear();
            foreach (var item in items)
                QuotaWindows.Add(item);

            // 칩 요약/경고는 선택된 표시 기준(QuotaChipWindow)으로 산출.
            UpdateQuotaSummary();
            HasQuota = true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 현재 <see cref="QuotaWindows"/>(이미 채워진 항목 VM)와 <see cref="QuotaChipWindow"/> 선택을
    /// 근거로 상단 칩의 <see cref="QuotaSummary"/>·<see cref="QuotaConstrained"/>를 산출한다.
    /// 네트워크 호출 없음 — UI 스레드(디스패처 또는 UI 컨텍스트)에서 호출돼야 한다.
    /// - "auto": 비무제한 중 잔여율 최소(가장 빡빡)를 기준. 전부 무제한이면 "무제한".
    /// - day/week/month: <see cref="QuotaWindowViewModel.Key"/>가 일치하는 윈도우를 기준.
    ///   없으면 auto로 폴백. 무제한이면 "{라벨} 무제한", 아니면 "{라벨} {잔여율}%".
    /// </summary>
    private void UpdateQuotaSummary()
    {
        if (QuotaWindows.Count == 0)
        {
            QuotaSummary = string.Empty;
            QuotaConstrained = false;
            return;
        }

        var explicitWindow = !string.IsNullOrEmpty(QuotaChipWindow)
            && !string.Equals(QuotaChipWindow, "auto", StringComparison.OrdinalIgnoreCase);

        QuotaWindowViewModel? selected;
        if (explicitWindow
            && QuotaWindows.FirstOrDefault(w => string.Equals(w.Key, QuotaChipWindow, StringComparison.OrdinalIgnoreCase)) is { } picked)
        {
            // 명시 선택(day/week/month) — 해당 윈도우가 무제한이어도 그 라벨을 보여준다.
            selected = picked;
        }
        else
        {
            // auto(또는 선택값 부재로 폴백) — 비무제한 중 잔여율 최소 = 가장 빡빡한 윈도우.
            selected = QuotaWindows
                .Where(w => !w.IsUnlimited)
                .OrderBy(w => w.PercentRemaining)
                .FirstOrDefault();
        }

        if (selected is null)
        {
            // 전부 무제한.
            QuotaSummary = "무제한";
            QuotaConstrained = false;
            return;
        }

        QuotaSummary = selected.IsUnlimited
            ? $"{selected.Label} 무제한"
            : $"{selected.Label} {selected.PercentRemaining:0}%";
        QuotaConstrained = selected.IsConstrained;
    }


    /// <summary>G — load action hints for the current workspace (stub: empty list).</summary>
    private async Task LoadSuggestionsAsync()
    {
        try
        {
            var items = await _suggestions.GetSuggestionsAsync(WorkspaceRoot).ConfigureAwait(false);
            await UiInvokeAsync(() =>
            {
                Suggestions.Clear();
                foreach (var s in items)
                    Suggestions.Add(s);
            }).ConfigureAwait(false);
        }
        catch
        {
            // Suggestions are best-effort; ignore failures (endpoint absent / offline).
        }
    }

    // ── Commands ───────────────────────────────────────────────────────

    /// <summary>
    /// 루프가 도는 동안에는 일반 프롬프트 전송을 막는다 — 같은 <see cref="AgentSession"/> 에 턴이
    /// 겹치면 대화 이력이 뒤섞인다. 슬래시 커맨드(/loop stop 등)는 빠져나갈 길이라 계속 허용한다.
    /// </summary>
    private bool CanSend()
        => !IsBusy
           && !string.IsNullOrWhiteSpace(InputText)
           && (!IsLoopRunning || InputText.TrimStart().StartsWith('/'));

    /// <summary>마지막으로 보낸 사용자 메시지(위 화살표/‎/retry 로 되불러 편집). 셸 히스토리처럼 단순 1개.</summary>
    public string LastSentMessage { get; private set; } = string.Empty;

    /// <summary>입력창의 @파일 자동완성(코드비하인드가 caret 과 함께 구동).</summary>
    public FileMentionViewModel FileMention { get; }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var goal = InputText.Trim();
        if (string.IsNullOrEmpty(goal)) return;

        // 슬래시 커맨드는 모델에 보내지 않고 클라이언트가 직접 처리한다.
        var slash = SlashCommands.Parse(goal);
        if (slash.Kind != SlashCommandKind.None)
        {
            await HandleSlashCommandAsync(slash).ConfigureAwait(true);
            return;
        }

        LastSentMessage = goal;
        InputText = string.Empty;
        FileMention.Close();   // 전송 시 열려 있던 @자동완성 닫기
        HasError = false;
        ErrorMessage = string.Empty;
        _currentAssistant = null; _currentThinking = null;
        _toolCards.Clear();
        _todos.Clear();   // 새 목표 — 이전 작업 계획 리셋(에이전트가 manage_todos로 다시 채운다).

        Transcript.Add(new UserTurnViewModel { Text = goal });

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        StatusText = "실행 중...";

        // D — 첨부 파일을 base64(≤10MiB)로 인코딩해 사용자 메시지에 싣는다. 인코딩 후 칩을 비운다.
        var attachmentPayloads = await BuildAttachmentPayloadsAsync(token).ConfigureAwait(false);
        await UiInvokeAsync(() => Attachments.Clear()).ConfigureAwait(false);

        await RunTurnAsync(goal, attachmentPayloads, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 턴 1회의 실행 본체 — 수동 전송과 /loop 반복이 공유한다(<see cref="LoopTurnRunner"/> 로 주입).
    /// 사용자 메시지 추가·첨부 인코딩 같은 준비는 호출자가 끝내고 들어온다.
    /// 예외를 밖으로 던지지 않고 성공 여부만 돌려준다 — 루프가 연속 실패를 세어 스스로 멈춰야 하기 때문.
    /// </summary>
    private async Task<LoopTurnOutcome> RunTurnAsync(
        string goal, IReadOnlyList<Attachment>? attachments, CancellationToken token)
    {
        _turnErrorMessage = null;

        // 루프 러너는 백그라운드 스레드에서 들어온다 — 상태 변경은 반드시 디스패처를 거친다.
        await UiInvokeAsync(() =>
        {
            IsBusy = true;
            StatusText = "실행 중...";
        }).ConfigureAwait(false);

        // 토큰 델타는 버퍼에 모아 ~50ms/1KB 간격으로 일괄 반영 — 매 토큰 디스패치+문자열 재레이아웃(O(n²)) 폭주 방지.
        var pendingText = new StringBuilder();
        var sinceFlush = Stopwatch.StartNew();

        async Task FlushTextAsync()
        {
            if (pendingText.Length == 0) return;
            var chunk = pendingText.ToString();
            pendingText.Clear();
            sinceFlush.Restart();
            await UiInvokeAsync(() => EnsureAssistant().Text += chunk).ConfigureAwait(false);
        }

        // 실패 사유(예외 경로). AgentError 이벤트 경로는 _turnErrorMessage 가 받는다.
        string? failure = null;

        try
        {
            await foreach (var evt in _orchestrator.RunAsync(goal, _session, attachments, token).ConfigureAwait(false))
            {
                if (evt is AgentTextDelta delta)
                {
                    pendingText.Append(delta.Text);
                    if (sinceFlush.ElapsedMilliseconds >= 50 || pendingText.Length >= 1024)
                        await FlushTextAsync().ConfigureAwait(false);
                    continue;
                }

                // 비-텍스트 이벤트는 순서 보존 위해 버퍼를 먼저 비운 뒤 반영.
                await FlushTextAsync().ConfigureAwait(false);
                var captured = evt;
                await UiInvokeAsync(() => Project(captured)).ConfigureAwait(false);
            }
            await FlushTextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            failure = "사용자가 중지했습니다.";
            await UiInvokeAsync(() =>
            {
                StatusText = "중지됨";
                Transcript.Add(new SystemNoticeViewModel { Text = "사용자가 중지했습니다." });
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            await UiInvokeAsync(() =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
                Transcript.Add(new SystemNoticeViewModel { Text = $"오류: {ex.Message}" });
            }).ConfigureAwait(false);
        }
        finally
        {
            await FlushTextAsync().ConfigureAwait(false);   // 취소/오류로 남은 버퍼 반영
            await UiInvokeAsync(() =>
            {
                if (_currentAssistant is { IsStreaming: true } a)
                    a.IsStreaming = false;
                IsBusy = false;
                // 턴이 끝나면 연결 표시로 되돌린다 — 안 그러면 "실행 중..."/"중지됨" 이 상단바에 눌러앉는다.
                StatusText = ConnectionStatusText;
                IterationDisplayText = string.Empty;   // 진행 중에만 의미 있는 값
            }).ConfigureAwait(false);

            // C — persist the (now-appended) session and refresh the sidebar list.
            await SaveCurrentSessionAsync().ConfigureAwait(false);
            await RefreshChatSessionsAsync().ConfigureAwait(false);

            // 턴 완료로 사용량이 변동 — best-effort 쿼터 재조회(턴당 1회).
            _ = RefreshQuotaAsync();
        }

        // AgentError 는 예외가 아니라 이벤트로 온다 — 여기서 합류시키지 않으면 루프가
        // "3연속 실패"를 영영 감지하지 못하고 오류만 찍으며 계속 돈다.
        var reason = failure ?? _turnErrorMessage;
        return reason is null ? LoopTurnOutcome.Ok() : LoopTurnOutcome.Fail(reason);
    }

    /// <summary>슬래시 커맨드 실행. 모델 호출 없이 클라이언트가 직접 처리한다.</summary>
    private async Task HandleSlashCommandAsync(SlashCommand cmd)
    {
        switch (cmd.Kind)
        {
            case SlashCommandKind.Clear:
                InputText = string.Empty;
                await ClearAsync().ConfigureAwait(true);
                break;

            case SlashCommandKind.Retry:
                // 마지막 메시지를 입력창으로 되불러 편집·재전송하게 한다(기존 이력은 건드리지 않는다).
                InputText = string.IsNullOrEmpty(LastSentMessage) ? InputText : LastSentMessage;
                break;

            case SlashCommandKind.Help:
                InputText = string.Empty;
                Transcript.Add(new SystemNoticeViewModel { Text = SlashCommands.HelpText });
                break;

            case SlashCommandKind.Loop:
                await HandleLoopCommandAsync(cmd.Loop ?? new LoopArgs(LoopAction.Status, null, string.Empty))
                    .ConfigureAwait(true);
                break;

            case SlashCommandKind.Exit:
                // 파서는 공유하지만 /exit 는 헤드리스(CLI) 전용 의미다. GUI 는 안내만 하고 넘긴다.
                InputText = string.Empty;
                Transcript.Add(new SystemNoticeViewModel
                {
                    Text = "/exit 는 헤드리스(CLI) 대화 모드 전용입니다. 창을 닫거나 트레이에서 종료하세요.",
                });
                break;

            case SlashCommandKind.Unknown:
                Transcript.Add(new SystemNoticeViewModel { Text = SlashCommands.UnknownText(cmd.Raw) });
                break;
        }
    }

    // ── /loop 반복 실행 ────────────────────────────────────────────────

    /// <summary>
    /// /loop 시작·중지·상태. 시작만 컨트롤러를 건드리고, 나머지는 안내 문구다.
    /// async 가 아니다 — 여기서 턴을 기다리면 입력이 얼어붙는다(TryStart 는 비차단).
    /// </summary>
    private Task HandleLoopCommandAsync(LoopArgs args)
    {
        InputText = string.Empty;

        switch (args.Action)
        {
            case LoopAction.Stop:
                _loop.Stop(LoopStopReason.UserStopped);
                Transcript.Add(new SystemNoticeViewModel { Text = "반복 실행을 중지했습니다." });
                break;

            case LoopAction.Status:
                Transcript.Add(new SystemNoticeViewModel
                {
                    Text = LoopStatusFormatter.DescribeDetailed(_loop.Status),
                });
                break;

            case LoopAction.Start:
                if (string.IsNullOrWhiteSpace(args.Prompt))
                {
                    Transcript.Add(new SystemNoticeViewModel
                    {
                        Text = "반복할 프롬프트가 필요합니다. 예: /loop 5m 빌드 상태 확인",
                    });
                    break;
                }

                // 루프 수명 전용 CTS — 개별 턴의 _cts 와 분리해야 "턴만 중지" 와 "루프 통째로 중지" 가 구분된다.
                _loopCts?.Dispose();
                _loopCts = new CancellationTokenSource();

                var request = new LoopStartRequest(
                    args.Interval is null ? LoopMode.Autonomous : LoopMode.FixedInterval,
                    args.Interval,
                    args.Prompt.Trim(),
                    _settings.Current.LoopMaxIterations);

                if (!_loop.TryStart(request, RunLoopTurnAsync, _loopCts.Token, out var error))
                {
                    Transcript.Add(new SystemNoticeViewModel
                    {
                        Text = error ?? "반복 실행을 시작하지 못했습니다.",
                    });
                }
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="LoopTurnRunner"/> 구현 — 컨트롤러가 매 반복 호출한다.
    /// 첨부는 싣지 않는다(루프는 텍스트 프롬프트만 반복한다는 설계 결정).
    /// </summary>
    private async Task<LoopTurnOutcome> RunLoopTurnAsync(LoopTurnContext ctx, CancellationToken ct)
    {
        await UiInvokeAsync(() =>
        {
            _currentAssistant = null; _currentThinking = null;
            _toolCards.Clear();
            Transcript.Add(new UserTurnViewModel
            {
                Text = $"[루프 {ctx.Iteration}/{ctx.MaxIterations}] {ctx.Prompt}",
            });
        }).ConfigureAwait(false);

        // 중지 버튼(_cts)이 "지금 도는 턴"도 끊을 수 있도록 루프 토큰에 연결해 둔다.
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        return await RunTurnAsync(ctx.Prompt, null, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>루프 이벤트 → 상단바/전사 반영. 컨트롤러가 백그라운드 스레드에서 발화한다.</summary>
    private void OnLoopChanged(object? sender, LoopEvent e) => _ = UiInvokeAsync(() =>
    {
        switch (e)
        {
            case LoopStarted:
            case LoopTurnStarting:
            case LoopWaiting:
            case LoopTick:
                IsLoopRunning = true;
                LoopStatusText = LoopStatusFormatter.Describe(_loop.Status);
                break;

            case LoopNotice notice:
                Transcript.Add(new SystemNoticeViewModel { Text = notice.Text });
                break;

            case LoopStopped stopped:
                IsLoopRunning = false;
                LoopStatusText = string.Empty;
                Transcript.Add(new SystemNoticeViewModel
                {
                    // 사유 문구는 Core 와 공유한다 — 헤드리스 stderr 와 표현이 갈리면 안 된다.
                    Text = $"반복 실행 종료 — {LoopStatusFormatter.StopReasonText(stopped.Reason)}, 총 {stopped.Iterations}회",
                });
                break;
        }
    });

    // ── 컴포저 "에이전트" 서랍 ─────────────────────────────────────────

    /// <summary>서랍의 "파일 첨부" — 파일 대화상자는 View 책임이라 코드비하인드에 요청만 보낸다.</summary>
    public event EventHandler? AttachFileRequested;

    /// <summary>입력창을 프리필한 뒤 포커스·캐럿을 넘겨달라는 요청(코드비하인드가 처리).</summary>
    public event EventHandler? ComposerFocusRequested;

    [RelayCommand]
    private void OpenAgentDrawer() => IsAgentDrawerOpen = true;

    [RelayCommand]
    private void CloseAgentDrawer() => IsAgentDrawerOpen = false;

    /// <summary>
    /// 서랍 "파일 첨부" — 첨부의 유일한 진입점이다. 파일 대화상자는 View 소유라
    /// 여기서는 <see cref="AttachFileRequested"/> 로 요청만 보내고, 코드비하인드가 고른 경로를
    /// <see cref="AddAttachmentPublic"/> 로 되돌려 준다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAttachFile))]
    private void RequestAttachFile()
    {
        IsAgentDrawerOpen = false;
        AttachFileRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>서랍 "마지막 메시지(/retry)" — 이력은 건드리지 않고 입력창에 되불러 편집하게 한다.</summary>
    [RelayCommand]
    private void RetryLast()
    {
        IsAgentDrawerOpen = false;
        if (string.IsNullOrEmpty(LastSentMessage))
        {
            Transcript.Add(new SystemNoticeViewModel { Text = "되불러올 이전 메시지가 없습니다." });
            return;
        }

        InputText = LastSentMessage;
        ComposerFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 서랍 "반복 실행(/loop)" — 여기서 바로 시작하지 않는다. 간격과 프롬프트는 사용자가 정해야 하므로
    /// 입력창에 "/loop " 만 깔아 주고 이어 치게 한다.
    /// </summary>
    [RelayCommand]
    private void StartLoop()
    {
        IsAgentDrawerOpen = false;
        InputText = "/loop ";
        ComposerFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>서랍 "도움말(/help)" — 입력창에 치지 않고 바로 안내를 전사에 남긴다.</summary>
    [RelayCommand]
    private void ShowHelp()
    {
        IsAgentDrawerOpen = false;
        Transcript.Add(new SystemNoticeViewModel { Text = SlashCommands.HelpText });
    }

    /// <summary>루프 대기 중에는 <see cref="IsBusy"/> 가 false 라 IsBusy 만 보면 중지 수단이 사라진다.</summary>
    private bool CanStop() => IsBusy || IsLoopRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        // 루프를 먼저 끊는다 — 턴만 취소하면 컨트롤러가 곧바로 다음 반복을 띄운다.
        _loop.Stop(LoopStopReason.UserStopped);
        _cts?.Cancel();
    }

    /// <summary>
    /// 로그아웃 시 호출 — 실행 중인 에이전트 작업을 취소하고 세션/화면 상태를 모두 초기화한다.
    /// (App이 모든 창을 닫고 로그인 화면으로 회귀시키기 직전에 사용.) UI 스레드에서 호출된다.
    /// </summary>
    public void PrepareForLogout()
    {
        // 루프를 먼저 끊는다 — 안 그러면 로그아웃 후에도 백그라운드에서 다음 턴이 뜬다(토큰 없이 실패 반복).
        _loop.Stop(LoopStopReason.UserStopped);
        try { _loopCts?.Cancel(); } catch { /* 이미 dispose됨 — 무시 */ }
        try { _cts?.Cancel(); } catch { /* 이미 dispose됨 — 무시 */ }

        IsLoopRunning = false;
        LoopStatusText = string.Empty;
        IsAgentDrawerOpen = false;

        Transcript.Clear();
        _toolCards.Clear();
        _currentAssistant = null; _currentThinking = null;
        _session = new AgentSession();
        Attachments.Clear();
        _permissions.ClearSessionRules();
        LastUsageText = string.Empty;
        IterationDisplayText = string.Empty;
        ResetUsageAccumulation();
        PendingApproval = null;
        IsBusy = false;
        UpdateNotice = string.Empty;
        UpdateMandatory = false;
        QuotaWindows.Clear();
        HasQuota = false;
    }

    /// <summary>배너의 주 버튼이 누를 동작 — 로그인 필요 시 로그인(설정) 열기, 아니면 재연결.</summary>
    [RelayCommand]
    private async Task ConnectionActionAsync()
    {
        if (NeedsLogin)
        {
            LoginRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        await RetryConnectionAsync().ConfigureAwait(false);
    }

    /// <summary>배너의 "로그인" 버튼이 요청 → View(또는 App)가 설정창을 연다.</summary>
    public event EventHandler? LoginRequested;

    [RelayCommand]
    private async Task RetryConnectionAsync()
    {
        StatusText = "연결 중...";

        ServerReadiness readiness;
        try
        {
            // ConfigureAwait(true) 필수 — ApplyReadiness 는 Unauthenticated 시 LoginRequested 를 통해
            // Transcript/Attachments.Clear() 와 Window 조작(App.ReturnToLogin)까지 유발한다.
            // 스레드풀에서 실행되면 ObservableCollection.Clear() 가 예외를 던지고, 이 Task 는
            // 호출부에서 버려져(_ =) 예외가 로그로만 남아 "로그인 화면으로 안 돌아감" 으로 나타난다.
            readiness = await _api.CheckReadinessAsync().ConfigureAwait(true);
        }
        catch
        {
            readiness = ServerReadiness.Disconnected;
        }

        ApplyReadiness(readiness);
    }

    /// <summary>서버에 닿아 있는지만 나타내는 상태 문구 — 인증 실패·실행 상태와 무관하게 이 값으로 고정한다.</summary>
    private string ConnectionStatusText => IsConnected ? "서버 연결됨" : "서버 연결 안됨";

    /// <summary>연결/인증 상태를 화면 상태(배너 제목·문구·버튼)로 반영한다.</summary>
    private void ApplyReadiness(ServerReadiness readiness)
    {
        IsConnected = readiness != ServerReadiness.Disconnected;
        NeedsLogin  = readiness == ServerReadiness.Unauthenticated;

        // 연결 표시는 도달 여부만 따른다 — 로그인이 필요해도 서버에는 닿았으므로 "서버 연결됨".
        StatusText = ConnectionStatusText;

        switch (readiness)
        {
            case ServerReadiness.Ready:
                HasError = false;
                ErrorMessage = string.Empty;
                break;

            case ServerReadiness.Unauthenticated:
                HasError = true;
                ErrorTitle = "로그인 필요";
                PrimaryActionText = "로그인";
                ErrorMessage = "서버에는 연결되었지만 로그인이 필요합니다.\n로그인 화면에서 다시 인증하세요.";
                // 로그인 필요 상황이면 즉시 모든 창을 닫고 로그인 화면만 남긴다(App이 처리).
                LoginRequested?.Invoke(this, EventArgs.Empty);
                break;

            default: // Disconnected
                HasError = true;
                ErrorTitle = "서버 연결 실패";
                PrimaryActionText = "다시 시도";
                ErrorMessage = $"에이전트 서버({_settings.Current.ServerBaseUrl})에 연결할 수 없습니다.\n서버가 실행 중인지 확인하세요.";
                break;
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        // 새 세션은 기존 루프를 무효로 만든다(반복 프롬프트가 빈 대화에 다시 꽂히면 맥락이 어긋난다).
        _loop.Stop(LoopStopReason.UserStopped);
        try { _loopCts?.Cancel(); } catch { /* 이미 dispose됨 — 무시 */ }

        // C — save the current session before starting a fresh chat.
        await SaveCurrentSessionAsync().ConfigureAwait(false);

        await UiInvokeAsync(() =>
        {
            IsLoopRunning = false;
            LoopStatusText = string.Empty;
            IsAgentDrawerOpen = false;
            Transcript.Clear();
            _toolCards.Clear();
            _currentAssistant = null; _currentThinking = null;
            _session = new AgentSession();
            Attachments.Clear();
            _permissions.ClearSessionRules();
            HasError = false;
            ErrorMessage = string.Empty;
            LastUsageText = string.Empty;
            IterationDisplayText = string.Empty;
            ResetUsageAccumulation();
            StatusText = ConnectionStatusText;
        }).ConfigureAwait(false);

        await RefreshChatSessionsAsync().ConfigureAwait(false);
    }

    // ── Phase D commands ───────────────────────────────────────────────

    private bool CanLoadChatSession(ChatSessionSummary? s) => s is not null && !IsBusy;

    /// <summary>C — save the current chat, then restore the selected session.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadChatSession))]
    private async Task LoadChatSessionAsync(ChatSessionSummary? summary)
    {
        if (summary is null) return;
        await SaveCurrentSessionAsync().ConfigureAwait(false);

        var record = await _chatHistory.LoadAsync(summary.Id).ConfigureAwait(false);
        if (record is null) return;

        await UiInvokeAsync(() => RestoreSession(record)).ConfigureAwait(false);
        await RefreshChatSessionsAsync().ConfigureAwait(false);
    }

    private bool CanDeleteChatSession(ChatSessionSummary? s) => s is not null;

    /// <summary>C — delete a saved session; clear the view if it is the active one.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteChatSession))]
    private async Task DeleteChatSessionAsync(ChatSessionSummary? summary)
    {
        if (summary is null) return;
        var wasActive = string.Equals(summary.Id, _session.Id, StringComparison.Ordinal);

        await _chatHistory.DeleteAsync(summary.Id).ConfigureAwait(false);

        // 서버에서도 삭제(fire-and-forget) — 다른 PC에서 다시 동기화되지 않도록. 실패는 무시(graceful).
        _ = _sessionSync.DeleteAsync(summary.Id);

        if (wasActive)
        {
            await UiInvokeAsync(() =>
            {
                Transcript.Clear();
                _toolCards.Clear();
                _currentAssistant = null; _currentThinking = null;
                _session = new AgentSession();
                Attachments.Clear();
                _permissions.ClearSessionRules();
                LastUsageText = string.Empty;
                IterationDisplayText = string.Empty;
                ResetUsageAccumulation();
            }).ConfigureAwait(false);
        }

        await RefreshChatSessionsAsync().ConfigureAwait(false);
    }

    private bool CanAttachFile() => !IsBusy;

    private bool CanRemoveAttachment(Attachment? a) => a is not null;

    /// <summary>D — remove a chip from the composer.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveAttachment))]
    private void RemoveAttachment(Attachment? attachment)
    {
        if (attachment is null) return;
        Attachments.Remove(attachment);
    }

    /// <summary>D — public entry point invoked by MainWindow code-behind after the file dialog.</summary>
    public void AddAttachmentPublic(string path) => AddAttachment(path);

    // ── Phase D private helpers ────────────────────────────────────────

    private void AddAttachment(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Ignore duplicate paths (case-insensitive).
        if (Attachments.Any(a => string.Equals(a.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        try
        {
            var meta = _attachmentService.CreateFromPath(path);
            Attachments.Add(meta);
        }
        catch (AgentException)
        {
            // File missing / unreadable — skip silently.
        }
    }

    /// <summary>
    /// D — 컴포저 첨부들을 전송용 페이로드로 변환한다. 각 파일을 base64로 인코딩(≤10MiB)해
    /// data_base64를 채운 Attachment 복사본을 만든다. 초과/읽기 실패 항목은 안내 후 제외.
    /// 첨부가 없으면 null(요청 바이트 불변).
    /// </summary>
    private async Task<IReadOnlyList<Attachment>?> BuildAttachmentPayloadsAsync(CancellationToken ct)
    {
        var sources = Attachments.ToList();
        if (sources.Count == 0) return null;

        var payloads = new List<Attachment>(sources.Count);
        foreach (var a in sources)
        {
            try
            {
                var base64 = await _attachmentService.ReadAsBase64Async(a, ct).ConfigureAwait(false);
                payloads.Add(a with { DataBase64 = base64 });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var name = a.FileName;
                var reason = ex is AgentException ? ex.Message : "읽기 실패";
                await UiInvokeAsync(() => Transcript.Add(new SystemNoticeViewModel
                {
                    Text = $"첨부 '{name}'(을)를 보내지 못했습니다 — {reason}"
                })).ConfigureAwait(false);
            }
        }

        return payloads.Count > 0 ? payloads : null;
    }

    /// <summary>C — upsert the current session to disk. No-op when there are no messages.</summary>
    private async Task SaveCurrentSessionAsync()
    {
        var messages = _session.Messages.ToList();
        if (messages.Count == 0) return;

        // 기존 레코드가 있으면 ProjectId(프로젝트 분류)와 CreatedUtc(최초 생성시각)를 보존한다.
        // 보존하지 않으면 분류한 대화를 이어 채팅할 때 미분류로 되돌아가는 버그가 생긴다.
        ChatSessionRecord? existing = null;
        try { existing = await _chatHistory.LoadAsync(_session.Id).ConfigureAwait(false); }
        catch { /* best-effort: 없으면 신규 취급 */ }

        var record = new ChatSessionRecord
        {
            Id = _session.Id,
            Title = _chatHistory.BuildTitle(messages),
            CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            WorkspaceRoot = WorkspaceRoot,
            ProjectId = existing?.ProjectId,
            Messages = messages,
        };

        try
        {
            await _chatHistory.SaveAsync(record).ConfigureAwait(false);

            // 서버 동기화로 push(fire-and-forget) — 여러 PC에서 대화를 공유한다. 실패는 무시(graceful).
            _ = _sessionSync.PushAsync(record);
        }
        catch
        {
            // Persistence is best-effort; do not surface IO failures to the user mid-turn.
        }
    }

    /// <summary>C — rebuild <see cref="ChatSessions"/> from local history (UI thread).</summary>
    private async Task RefreshChatSessionsAsync()
    {
        IReadOnlyList<ChatSessionSummary> list;
        try
        {
            list = await _chatHistory.ListAsync().ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        await UiInvokeAsync(() =>
        {
            ChatSessions.Clear();
            foreach (var s in list)
                ChatSessions.Add(s);
        }).ConfigureAwait(false);

        // #4 — 사이드바 프로젝트 그룹도 함께 갱신(삭제·신규·편입이 즉시 반영되도록 단일 갱신 지점).
        if (Projects is { } projects)
            await projects.LoadAsync().ConfigureAwait(false);
    }

    /// <summary>C — replace the active session and re-project its transcript (UI thread).</summary>
    private void RestoreSession(ChatSessionRecord record)
    {
        _session = new AgentSession(record.Id, record.Messages);

        Transcript.Clear();
        _toolCards.Clear();
        _currentAssistant = null; _currentThinking = null;
        Attachments.Clear();
        HasError = false;
        ErrorMessage = string.Empty;
        LastUsageText = string.Empty;
        IterationDisplayText = string.Empty;
        ResetUsageAccumulation();
        StatusText = IsConnected ? "Connected" : "Disconnected";

        // Map persisted messages back onto transcript cards. Tool messages pair
        // with the preceding assistant ToolCalls (best-effort per §4.1.1).
        foreach (var msg in record.Messages)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                    Transcript.Add(new UserTurnViewModel { Text = msg.Content ?? string.Empty });
                    break;

                case MessageRole.Assistant:
                    if (!string.IsNullOrEmpty(msg.Content))
                    {
                        Transcript.Add(new AssistantTurnViewModel
                        {
                            Text = msg.Content!,
                            IsStreaming = false,
                        });
                    }

                    if (msg.ToolCalls is { Count: > 0 } calls)
                    {
                        foreach (var call in calls)
                        {
                            var card = new ToolCallViewModel
                            {
                                CallId = call.Id,
                                ToolName = call.Name,
                                ArgsPreview = RenderArgs(call.Arguments),
                                Status = ToolCallStatus.Succeeded,
                            };
                            _toolCards[call.Id] = card;
                            Transcript.Add(card);
                        }
                    }
                    break;

                case MessageRole.Tool:
                    if (msg.ToolCallId is { } id && _toolCards.TryGetValue(id, out var toolCard))
                    {
                        var isError = msg.IsError ?? false;
                        toolCard.ResultText = msg.Content ?? string.Empty;
                        toolCard.IsError = isError;
                        toolCard.Status = isError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
                    }
                    break;

                case MessageRole.System:
                    // System prompt is not surfaced in the transcript.
                    break;
            }
        }
    }

    // ── Approval surfacing (PermissionService handler) ─────────────────

    private async Task<PermissionDecision> RequestApprovalAsync(
        ToolCall call, ToolRisk risk, CancellationToken ct)
    {
        var vm = new ApprovalRequestViewModel(call.Name, risk, RenderArgs(call.Arguments), call.Arguments);
        await UiInvokeAsync(() => PendingApproval = vm).ConfigureAwait(false);
        try
        {
            return await vm.WaitForDecisionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await UiInvokeAsync(() => PendingApproval = null).ConfigureAwait(false);
        }
    }

    // ── AgentEvent -> Transcript projection (UI thread) ────────────────

    private void Project(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentTextDelta d:
                EnsureAssistant().Text += d.Text;
                break;

            case AgentThinkingDelta td:
                EnsureThinking().Text += td.Text;
                break;

            case AgentThinkingComplete:
                if (_currentThinking is { } tvm)
                {
                    tvm.IsStreaming = false;
                    tvm.IsExpanded = false;   // 사고가 끝나면 접어 최종 답변을 가리지 않는다.
                    _currentThinking = null;
                }
                break;

            case AgentAssistantMessageComplete:
                if (_currentAssistant is { } a)
                {
                    a.IsStreaming = false;
                    _currentAssistant = null; _currentThinking = null;
                }
                break;

            case AgentToolCallStarted started:
            {
                var card = new ToolCallViewModel
                {
                    CallId = started.CallId,
                    ToolName = started.ToolName,
                    Risk = started.Risk,
                    ArgsPreview = RenderArgs(started.Args),
                    Status = ToolCallStatus.Running,
                };
                _toolCards[started.CallId] = card;
                Transcript.Add(card);
                break;
            }

            case AgentAwaitingApproval awaiting:
                if (_toolCards.TryGetValue(awaiting.CallId, out var awaitingCard))
                    awaitingCard.Status = ToolCallStatus.AwaitingApproval;
                break;

            case AgentToolCallResult result:
                if (_toolCards.TryGetValue(result.CallId, out var resultCard))
                {
                    resultCard.ResultText = result.Result.Content;
                    resultCard.IsError = result.Result.IsError;
                    resultCard.Status = result.Result.IsError
                        ? (result.Result.Content == "Denied by user"
                            ? ToolCallStatus.Denied
                            : ToolCallStatus.Failed)
                        : ToolCallStatus.Succeeded;
                }
                break;

            case AgentIterationAdvanced iter:
                IterationDisplayText = $"반복 {iter.Iteration}/{iter.MaxIterations}";
                break;

            case AgentDone done:
                if (_currentAssistant is { } finalAssistant)
                {
                    finalAssistant.IsStreaming = false;
                    _currentAssistant = null; _currentThinking = null;
                }
                StatusText = "완료";
                if (done.LastUsage is { } usage)
                {
                    // 단위를 명시한다 — "in:1234" 만으로는 무슨 수치인지 알 수 없다.
                    LastUsageText = $"in {usage.PromptTokens:N0} · out {usage.CompletionTokens:N0} 토큰";
                    // Feature 7 — 세션 누적 총 토큰량 갱신(클릭 토글 표시용).
                    _totalTokens += usage.PromptTokens + usage.CompletionTokens;
                    OnPropertyChanged(nameof(UsageDisplayText));
                }
                break;

            case AgentNotice notice:
                // 오류가 아니다 — 전사에만 남긴다. HasError/ErrorTitle/"다시 시도"를 건드리지 않는다.
                Transcript.Add(new SystemNoticeViewModel { Text = notice.Text });
                break;

            case AgentError err:
                // 이 턴은 실패로 기록한다 — 루프의 연속 실패 차단이 이 흔적을 근거로 스스로 멈춘다.
                _turnErrorMessage = string.IsNullOrWhiteSpace(err.Message) ? (err.Code ?? "오류") : err.Message;

                // API-SPEC §클라이언트 처리 가이드: HTTP 상태(코드)로 분기. 401에서만 재로그인,
                // 403/429/404/5xx는 메시지만 표시하고 로그아웃하지 않는다.
                switch (ClassifyAgentError(err.Code))
                {
                    case AgentErrorKind.Cancelled:
                        StatusText = "중지됨";
                        break;

                    case AgentErrorKind.Unauthorized:
                        // 401 만 재로그인(세션 폐기). ApplyReadiness가 LoginRequested→로그인 화면 회귀.
                        ApplyReadiness(ServerReadiness.Unauthenticated);
                        break;

                    case AgentErrorKind.RateLimited:
                        // 429 토큰 쿼터/세션 캡 초과 — 친화 문구로 안내(서버 원문 비노출), 로그아웃 금지.
                        StatusText = "사용 한도 초과";
                        Transcript.Add(new SystemNoticeViewModel
                        {
                            Text = "⚠ " + UserErrorMessages.ForAgentError(err.Code, err.Message)
                        });
                        _ = RefreshQuotaAsync();   // 잔여량 즉시 갱신
                        break;

                    case AgentErrorKind.Forbidden:
                        // 403 권한 부족 — 친화 문구 안내, 로그아웃 금지.
                        StatusText = "권한 없음";
                        Transcript.Add(new SystemNoticeViewModel
                        {
                            Text = UserErrorMessages.ForAgentError(err.Code, err.Message)
                        });
                        break;

                    default:
                        // 400/404/500/502/기타 — 친화 문구만 표시(서버 원문 비노출), 로그아웃 금지.
                        var friendly = UserErrorMessages.ForAgentError(err.Code, err.Message);
                        HasError = true;
                        ErrorTitle = "오류";
                        PrimaryActionText = "다시 시도";
                        ErrorMessage = friendly;
                        StatusText = "오류";
                        Transcript.Add(new SystemNoticeViewModel { Text = friendly });
                        break;
                }
                break;
        }
    }

    /// <summary>
    /// Feature 7 — 세션 초기화(새 채팅·로그아웃·세션 복원/삭제) 시 토큰 표시 상태를 리셋한다.
    /// 누적 총량을 0으로 되돌리고 in/out 표시 모드로 복귀시킨다. UI 스레드에서 호출.
    /// </summary>
    private void ResetUsageAccumulation()
    {
        _totalTokens = 0;
        ShowTotalUsage = false;          // NotifyPropertyChangedFor가 UsageDisplayText 발화
        OnPropertyChanged(nameof(UsageDisplayText));
    }

    private AssistantTurnViewModel EnsureAssistant()
    {
        if (_currentAssistant is { IsStreaming: true } existing)
            return existing;

        var a = new AssistantTurnViewModel { IsStreaming = true };
        _currentAssistant = a;
        Transcript.Add(a);
        return a;
    }

    private ThinkingViewModel EnsureThinking()
    {
        if (_currentThinking is { IsStreaming: true } existing)
            return existing;

        var t = new ThinkingViewModel { IsStreaming = true };
        _currentThinking = t;
        Transcript.Add(t);
        return t;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private enum AgentErrorKind { Cancelled, Unauthorized, Forbidden, RateLimited, Other }

    /// <summary>
    /// 서버 에러를 HTTP 상태/중첩 envelope 코드 기준으로 분류한다(API-SPEC §클라이언트 처리 가이드).
    /// **메시지 본문으로 판별하지 않는다** — 429 쿼터 메시지의 "token"·"quota" 등을 인증 실패로 오인하면
    /// 토큰이 유효한데도 로그인 화면으로 튕기는 흔한 버그가 생긴다(스펙 경고).
    /// 코드 형태: 사전(pre-stream) "http_401"·"http_429", 스트림 중 "unauthorized"·"rate_limited" 등.
    /// </summary>
    private static AgentErrorKind ClassifyAgentError(string? code)
    {
        var c = code ?? string.Empty;
        if (c.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return AgentErrorKind.Cancelled;
        if (c.Contains("429", StringComparison.OrdinalIgnoreCase)
            || c.Contains("rate_limited", StringComparison.OrdinalIgnoreCase)
            || c.Contains("too_many", StringComparison.OrdinalIgnoreCase))
            return AgentErrorKind.RateLimited;
        if (c.Contains("401", StringComparison.OrdinalIgnoreCase)
            || c.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return AgentErrorKind.Unauthorized;
        if (c.Contains("403", StringComparison.OrdinalIgnoreCase)
            || c.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
            return AgentErrorKind.Forbidden;
        return AgentErrorKind.Other;
    }

    /// <summary>Pretty-prints tool-call JSON arguments for display.</summary>
    private static string RenderArgs(JsonElement args)
    {
        try
        {
            if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return string.Empty;
            return JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return args.ToString();
        }
    }

    /// <summary>UI 스레드 마샬링(공용 <see cref="UiDispatch"/> 위임). 오케스트레이터 스트림이 UI 밖에서 돌므로 Transcript/속성 변경이 이곳을 거친다.</summary>
    private static Task UiInvokeAsync(Action action) => UiDispatch.InvokeAsync(action);

    // manage_todos 도구가 계획을 갱신하면(백그라운드 스레드일 수 있음) UI 스레드에서 Todos 컬렉션을 재구성한다.
    private void OnTodosChanged(object? sender, IReadOnlyList<TodoItem> items)
        => _ = UiInvokeAsync(() =>
        {
            Todos.Clear();
            foreach (var t in items) Todos.Add(t);
            HasTodos = Todos.Count > 0;
        });

    /// <summary>
    /// 앱 종료 시(App.DisposeAll) 1회 정리 — 싱글턴 서비스 이벤트 구독을 해제하고 마지막
    /// <see cref="_cts"/> 를 반납한다. 재로그인 경로에서는 이 VM이 재사용되므로 호출하지 않는다
    /// (PrepareForLogout 만 실행 중 작업을 취소). DisposeAll 이 종료 경로에서 두 번 호출될 수
    /// 있으나, <c>-=</c> 와 <see cref="CancellationTokenSource.Dispose"/> 는 모두 멱등이라 안전하다.
    /// </summary>
    public void Dispose()
    {
        _unsubscribe();

        // 루프 컨트롤러는 이 VM 이 소유한다(App.DisposeAll 은 따로 건드리지 않는다).
        // Dispose 가 내부 CTS 취소 + 루프 Task join 까지 책임진다 — 종료 후 살아남는 반복이 없어야 한다.
        try { _loop.Dispose(); } catch { /* 종료 중 — 무시 */ }
        try { _loopCts?.Dispose(); } catch { /* 종료 중 — 무시 */ }

        // 실행 중 취소 흐름(Stop/PrepareForLogout 의 _cts?.Cancel())은 그대로 두고,
        // 마지막으로 남은 CTS 인스턴스만 반납한다. 종료 중 경합/이미-dispose는 무시.
        try { _cts?.Dispose(); } catch { /* 종료 중 — 무시 */ }
    }
}
