using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.ViewModels.Transcript;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// Root ViewModel driving the autonomous agent loop. Replaces the retired
/// <c>MainViewModel</c>. Consumes <see cref="IAgentOrchestrator"/> and projects
/// its <c>AgentEvent</c> stream onto a transcript of <see cref="ITranscriptItem"/>s,
/// marshalling all UI mutations onto the WPF dispatcher.
/// </summary>
public sealed partial class AgentSessionViewModel : ObservableObject
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAgentApiClient _api;
    private readonly IPermissionService _permissions;
    private readonly IWorkspaceContext _workspace;
    private readonly ISettingsService _settings;
    private readonly IWorkspaceHistoryService _workspaceHistory;
    private readonly IChatHistoryService _chatHistory;
    private readonly IFileAttachmentService _attachmentService;
    private readonly ISuggestionService _suggestions;
    private readonly IToolPolicyService _policy;

    private AgentSession _session = new();
    private CancellationTokenSource? _cts;

    // Fast lookup from tool CallId -> its transcript card.
    private readonly Dictionary<string, ToolCallViewModel> _toolCards = new();

    // Current open streaming assistant turn (null between turns).
    private AssistantTurnViewModel? _currentAssistant;

    // Guards re-entrant settings writes when seeding properties from settings.
    private bool _suppressPersist;

    // ── Observable state ───────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkspaceCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadChatSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AttachFileCommand))]
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
    [ObservableProperty] private string _lastUsageText = string.Empty;

    // ── Phase D observable state ───────────────────────────────────────

    /// <summary>F — greeting heading: "{name}님 안녕하세요, 어떤 업무를 시작할까요?".</summary>
    [ObservableProperty] private string _greetingText = string.Empty;

    /// <summary>F — raw display name (settings UserDisplayName ?? Environment.UserName).</summary>
    [ObservableProperty] private string _userDisplayName = string.Empty;

    /// <summary>D — convenience flag mirroring <c>Attachments.Count &gt; 0</c>.</summary>
    [ObservableProperty] private bool _hasAttachments;

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

    // ── Collections ────────────────────────────────────────────────────

    public ObservableCollection<ITranscriptItem> Transcript { get; } = [];

    /// <summary>B — recent workspace history shown in the sidebar "프로젝트" list.</summary>
    public ObservableCollection<WorkspaceHistoryEntry> RecentWorkspaces { get; } = [];

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
        IWorkspaceHistoryService workspaceHistory,
        IChatHistoryService chatHistory,
        IFileAttachmentService attachments,
        ISuggestionService suggestions,
        IToolPolicyService policy)
    {
        _orchestrator = orchestrator;
        _api = api;
        _permissions = permissions;
        _workspace = workspace;
        _settings = settings;
        _workspaceHistory = workspaceHistory;
        _chatHistory = chatHistory;
        _attachmentService = attachments;
        _suggestions = suggestions;
        _policy = policy;

        // Surface Manual-mode approvals through the inline approval card.
        _permissions.SetApprovalHandler(RequestApprovalAsync);

        // B — keep the sidebar workspace list in sync with persisted history.
        _workspaceHistory.HistoryChanged += OnWorkspaceHistoryChanged;

        // #3 — 설정(작업 디렉토리 추가/토글/제거)이 바뀌면 메인 칩·인사말을 즉시 갱신.
        //       SettingsService가 UI 디스패처로 발화하므로 핸들러는 UI 스레드에서 실행된다.
        _settings.SettingsChanged += OnSettingsChanged;

        // D — mirror attachment count onto HasAttachments.
        Attachments.CollectionChanged += (_, _) => HasAttachments = Attachments.Count > 0;

        SeedFromSettings();
        RefreshWorkspaceList();
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

            // F — greeting from settings display name, falling back to the OS user name.
            var rawName = string.IsNullOrWhiteSpace(s.UserDisplayName) ? Environment.UserName : s.UserDisplayName;
            UserDisplayName = rawName;
            GreetingText = $"{rawName}님 안녕하세요, 어떤 업무를 시작할까요?";
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    public async Task InitializeAsync()
    {
        SeedFromSettings();
        RefreshWorkspaceList();
        await RetryConnectionAsync();
        if (IsConnected && !NeedsLogin)
        {
            Transcript.Add(new SystemNoticeViewModel
            {
                Text = "에이전트 서버에 연결되었습니다. 무엇을 도와드릴까요?",
            });

            // 로그인 직후 1회 — 서버 도구 정책(모드+목록) 로드. best-effort(서버 미구현이면 fail-open).
            try { await _policy.LoadAsync().ConfigureAwait(true); }
            catch { /* graceful — 정책 로드 실패가 앱 동작을 막지 않는다(정책 부재=전체 허용). */ }

            // 연결 성공 후 best-effort 버전 점검(서버 미구현이면 조용히 무시).
            _ = CheckVersionAsync();

            // 연결+인증 직후 best-effort 쿼터 로드(서버 미응답이면 조용히 숨김).
            _ = RefreshQuotaAsync();
        }

        // C — populate the sidebar chat list + project groups from local history.
        await RefreshChatSessionsAsync();

        // G — fetch action hints (currently a stubbed empty list).
        _ = LoadSuggestionsAsync();
    }

    /// <summary>
    /// 재로그인 시 서버 도구 정책(모드+목록)을 다시 로드한다. App.xaml.cs의 ReopenLogin 성공 핸들러가
    /// 재연결 완료 후 호출한다. 일반 RetryConnection(단순 재연결)에서는 호출하지 않는다 — 세션 중 모드 안정.
    /// </summary>
    public Task ReloadToolPolicyAsync() => _policy.LoadAsync();

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
    /// </summary>
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

        // 가장 빡빡한 윈도우 = 비무제한 중 잔여율 최소. 전부 무제한이면 null.
        var tightest = items
            .Where(i => !i.IsUnlimited)
            .OrderBy(i => i.PercentRemaining)
            .FirstOrDefault();

        var summary = tightest is null
            ? "무제한"
            : $"{tightest.Label} {tightest.PercentRemaining:0}%";
        var constrained = tightest is { IsConstrained: true };

        await UiInvokeAsync(() =>
        {
            QuotaWindows.Clear();
            foreach (var item in items)
                QuotaWindows.Add(item);

            QuotaSummary = summary;
            QuotaConstrained = constrained;
            HasQuota = true;
        }).ConfigureAwait(false);
    }

    /// <summary>B — refresh <see cref="RecentWorkspaces"/> from the history service snapshot.</summary>
    private void RefreshWorkspaceList()
    {
        var recent = _workspaceHistory.GetRecent();
        RecentWorkspaces.Clear();
        foreach (var w in recent)
            RecentWorkspaces.Add(w);
    }

    private void OnWorkspaceHistoryChanged(object? sender, EventArgs e)
        => _ = UiInvokeAsync(RefreshWorkspaceList);

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

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var goal = InputText.Trim();
        if (string.IsNullOrEmpty(goal)) return;

        InputText = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
        _currentAssistant = null;
        _toolCards.Clear();

        Transcript.Add(new UserTurnViewModel { Text = goal });

        // D — attachments are managed/displayed client-side only. Actual payload
        // attachment to the outgoing message is deferred until the §8 server
        // contract is finalized (orchestrator signature is kept unchanged), so we
        // simply clear the composer chips after dispatching the turn.
        Attachments.Clear();

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        StatusText = "실행 중...";
        try
        {
            await foreach (var evt in _orchestrator.RunAsync(goal, _session, token).ConfigureAwait(false))
            {
                var captured = evt;
                await UiInvokeAsync(() => Project(captured)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await UiInvokeAsync(() =>
            {
                StatusText = "중지됨";
                Transcript.Add(new SystemNoticeViewModel { Text = "사용자가 중지했습니다." });
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
                Transcript.Add(new SystemNoticeViewModel { Text = $"오류: {ex.Message}" });
            }).ConfigureAwait(false);
        }
        finally
        {
            await UiInvokeAsync(() =>
            {
                if (_currentAssistant is { IsStreaming: true } a)
                    a.IsStreaming = false;
                IsBusy = false;
            }).ConfigureAwait(false);

            // C — persist the (now-appended) session and refresh the sidebar list.
            await SaveCurrentSessionAsync().ConfigureAwait(false);
            await RefreshChatSessionsAsync().ConfigureAwait(false);

            // 턴 완료로 사용량이 변동 — best-effort 쿼터 재조회(턴당 1회).
            _ = RefreshQuotaAsync();
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    /// <summary>
    /// 로그아웃 시 호출 — 실행 중인 에이전트 작업을 취소하고 세션/화면 상태를 모두 초기화한다.
    /// (App이 모든 창을 닫고 로그인 화면으로 회귀시키기 직전에 사용.) UI 스레드에서 호출된다.
    /// </summary>
    public void PrepareForLogout()
    {
        try { _cts?.Cancel(); } catch { /* 이미 dispose됨 — 무시 */ }

        Transcript.Clear();
        _toolCards.Clear();
        _currentAssistant = null;
        _session = new AgentSession();
        Attachments.Clear();
        _permissions.ClearSessionRules();
        LastUsageText = string.Empty;
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
            readiness = await _api.CheckReadinessAsync().ConfigureAwait(false);
        }
        catch
        {
            readiness = ServerReadiness.Disconnected;
        }

        ApplyReadiness(readiness);
    }

    /// <summary>연결/인증 상태를 화면 상태(배너 제목·문구·버튼)로 반영한다.</summary>
    private void ApplyReadiness(ServerReadiness readiness)
    {
        IsConnected = readiness != ServerReadiness.Disconnected;
        NeedsLogin  = readiness == ServerReadiness.Unauthenticated;

        switch (readiness)
        {
            case ServerReadiness.Ready:
                HasError = false;
                ErrorMessage = string.Empty;
                StatusText = "연결됨";
                break;

            case ServerReadiness.Unauthenticated:
                HasError = true;
                ErrorTitle = "로그인 필요";
                PrimaryActionText = "로그인";
                StatusText = "로그인 필요";
                ErrorMessage = "서버에는 연결되었지만 로그인이 필요합니다.\n로그인 화면에서 다시 인증하세요.";
                // 로그인 필요 상황이면 즉시 모든 창을 닫고 로그인 화면만 남긴다(App이 처리).
                LoginRequested?.Invoke(this, EventArgs.Empty);
                break;

            default: // Disconnected
                HasError = true;
                ErrorTitle = "서버 연결 실패";
                PrimaryActionText = "다시 시도";
                StatusText = "연결 끊김";
                ErrorMessage = $"에이전트 서버({_settings.Current.ServerBaseUrl})에 연결할 수 없습니다.\n서버가 실행 중인지 확인하세요.";
                break;
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        // C — save the current session before starting a fresh chat.
        await SaveCurrentSessionAsync().ConfigureAwait(false);

        await UiInvokeAsync(() =>
        {
            Transcript.Clear();
            _toolCards.Clear();
            _currentAssistant = null;
            _session = new AgentSession();
            Attachments.Clear();
            _permissions.ClearSessionRules();
            HasError = false;
            ErrorMessage = string.Empty;
            LastUsageText = string.Empty;
            StatusText = IsConnected ? "Connected" : "Disconnected";
        }).ConfigureAwait(false);

        await RefreshChatSessionsAsync().ConfigureAwait(false);
    }

    // ── Phase D commands ───────────────────────────────────────────────

    private bool CanOpenWorkspace(WorkspaceHistoryEntry? e) => e is not null && !IsBusy;

    /// <summary>B — switch the active workspace to a history entry.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenWorkspace))]
    private async Task OpenWorkspaceAsync(WorkspaceHistoryEntry? entry)
    {
        if (entry is null) return;
        _workspace.SetRoot(entry.Path);
        await _settings.UpdateWorkspaceRootAsync(entry.Path).ConfigureAwait(false);
        await _workspaceHistory.TouchAsync(entry.Path).ConfigureAwait(false);
        await UiInvokeAsync(() =>
        {
            WorkspaceRoot = entry.Path;
            var rawName = string.IsNullOrWhiteSpace(_settings.Current.UserDisplayName)
                ? Environment.UserName
                : _settings.Current.UserDisplayName;
            GreetingText = $"{rawName}님 안녕하세요, 어떤 업무를 시작할까요?";
        }).ConfigureAwait(false);
    }

    private bool CanRemoveWorkspace(WorkspaceHistoryEntry? e) => e is not null;

    /// <summary>B — drop a workspace from history.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveWorkspace))]
    private async Task RemoveWorkspaceAsync(WorkspaceHistoryEntry? entry)
    {
        if (entry is null) return;
        await _workspaceHistory.RemoveAsync(entry.Path).ConfigureAwait(false);
    }

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

        if (wasActive)
        {
            await UiInvokeAsync(() =>
            {
                Transcript.Clear();
                _toolCards.Clear();
                _currentAssistant = null;
                _session = new AgentSession();
                Attachments.Clear();
                _permissions.ClearSessionRules();
                LastUsageText = string.Empty;
            }).ConfigureAwait(false);
        }

        await RefreshChatSessionsAsync().ConfigureAwait(false);
    }

    private bool CanAttachFile() => !IsBusy;

    /// <summary>
    /// D — gate-only command for the composer "+" button. The actual
    /// <c>OpenFileDialog</c> is invoked from MainWindow code-behind, which then
    /// calls <see cref="AddAttachmentPublic"/> for each selected path.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAttachFile))]
    private void AttachFile()
    {
        // No-op: dialog is owned by the View (MVVM-safe). See AddAttachmentPublic.
    }

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
        _currentAssistant = null;
        Attachments.Clear();
        HasError = false;
        ErrorMessage = string.Empty;
        LastUsageText = string.Empty;
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
        var vm = new ApprovalRequestViewModel(call.Name, risk, RenderArgs(call.Arguments));
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

            case AgentAssistantMessageComplete:
                if (_currentAssistant is { } a)
                {
                    a.IsStreaming = false;
                    _currentAssistant = null;
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
                StatusText = $"실행 중 ({iter.Iteration}/{iter.MaxIterations})";
                break;

            case AgentDone done:
                if (_currentAssistant is { } finalAssistant)
                {
                    finalAssistant.IsStreaming = false;
                    _currentAssistant = null;
                }
                StatusText = "완료";
                if (done.LastUsage is { } usage)
                    LastUsageText = $"in:{usage.PromptTokens} out:{usage.CompletionTokens}";
                break;

            case AgentError err:
                if (IsAuthError(err.Code, err.Message))
                {
                    // 토큰 없음/만료 → "오류"가 아니라 "로그인 필요" 상태로 전환.
                    // ApplyReadiness가 LoginRequested를 올려 App이 모든 창을 닫고 로그인 화면으로 회귀시킨다.
                    ApplyReadiness(ServerReadiness.Unauthenticated);
                }
                else
                {
                    HasError = true;
                    ErrorTitle = "오류";
                    PrimaryActionText = "다시 시도";
                    ErrorMessage = err.Message;
                    StatusText = "오류";
                    Transcript.Add(new SystemNoticeViewModel { Text = $"오류 [{err.Code}]: {err.Message}" });
                }
                break;
        }
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

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>서버의 인증 실패(401/403, missing/invalid bearer token)인지 판별.</summary>
    private static bool IsAuthError(string? code, string? message)
    {
        var c = code ?? string.Empty;
        var m = message ?? string.Empty;
        return c.Contains("401", StringComparison.OrdinalIgnoreCase)
            || c.Contains("403", StringComparison.OrdinalIgnoreCase)
            || c.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || c.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || m.Contains("bearer", StringComparison.OrdinalIgnoreCase)
            || m.Contains("token", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Marshals an action onto the WPF UI thread. The orchestrator stream runs
    /// off the UI thread, so every Transcript / property mutation flows through here.
    /// </summary>
    private static Task UiInvokeAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
