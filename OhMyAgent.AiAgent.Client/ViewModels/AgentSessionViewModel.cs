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
    private readonly IChatHistoryService _chatHistory;
    private readonly IFileAttachmentService _attachmentService;
    private readonly ISuggestionService _suggestions;
    private readonly IToolPolicyService _policy;
    private readonly ISessionSyncService _sessionSync;
    private readonly ITodoService _todos;

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

    /// <summary>상단 토큰 표시를 in/out ↔ 세션 누적 총량으로 토글(텍스트 클릭이 바인딩).</summary>
    [RelayCommand]
    private void ToggleUsageView() => ShowTotalUsage = !ShowTotalUsage;

    // ── Phase D observable state ───────────────────────────────────────

    /// <summary>F — greeting heading: "{name}님 안녕하세요, 어떤 업무를 시작할까요?".</summary>
    [ObservableProperty] private string _greetingText = string.Empty;

    /// <summary>F — raw display name (settings UserDisplayName ?? Environment.UserName).</summary>
    [ObservableProperty] private string _userDisplayName = string.Empty;

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
        ITodoService todos)
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

        // 도구(manage_todos)가 백그라운드 스레드에서 갱신 → UI 디스패처로 마샬해 Todos 컬렉션 재구성.
        _todos.TodosChanged += OnTodosChanged;

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

            // 로그인 직후 1회 — 서버 도구 정책(모드+목록) 로드. best-effort(서버 미구현이면 fail-open).
            try { await _policy.LoadAsync().ConfigureAwait(true); }
            catch { /* graceful — 정책 로드 실패가 앱 동작을 막지 않는다(정책 부재=전체 허용). */ }

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

        try
        {
            await foreach (var evt in _orchestrator.RunAsync(goal, _session, attachmentPayloads, token).ConfigureAwait(false))
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
            await FlushTextAsync().ConfigureAwait(false);   // 취소/오류로 남은 버퍼 반영
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
            ResetUsageAccumulation();
            StatusText = IsConnected ? "Connected" : "Disconnected";
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
                _currentAssistant = null;
                _session = new AgentSession();
                Attachments.Clear();
                _permissions.ClearSessionRules();
                LastUsageText = string.Empty;
                ResetUsageAccumulation();
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
        _currentAssistant = null;
        Attachments.Clear();
        HasError = false;
        ErrorMessage = string.Empty;
        LastUsageText = string.Empty;
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
                {
                    LastUsageText = $"in:{usage.PromptTokens} out:{usage.CompletionTokens}";
                    // Feature 7 — 세션 누적 총 토큰량 갱신(클릭 토글 표시용).
                    _totalTokens += usage.PromptTokens + usage.CompletionTokens;
                    OnPropertyChanged(nameof(UsageDisplayText));
                }
                break;

            case AgentError err:
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
}
