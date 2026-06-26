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

    /// <summary>Selectable permission modes for the header selector.</summary>
    public IReadOnlyList<PermissionMode> PermissionModes { get; } =
        [PermissionMode.Manual, PermissionMode.AutoSafe, PermissionMode.FullAuto];

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
        ISuggestionService suggestions)
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

        // Surface Manual-mode approvals through the inline approval card.
        _permissions.SetApprovalHandler(RequestApprovalAsync);

        // B — keep the sidebar workspace list in sync with persisted history.
        _workspaceHistory.HistoryChanged += OnWorkspaceHistoryChanged;

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

    private void SeedFromSettings()
    {
        _suppressPersist = true;
        try
        {
            var s = _settings.Current;
            WorkspaceRoot = string.IsNullOrWhiteSpace(s.WorkspaceRoot) ? _workspace.Root : s.WorkspaceRoot;
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
            Transcript.Add(new SystemNoticeViewModel
            {
                Text = "에이전트 서버에 연결되었습니다. 무엇을 도와드릴까요?",
            });

        // C — populate the sidebar chat list from local history.
        await RefreshChatSessionsAsync();

        // G — fetch action hints (currently a stubbed empty list).
        _ = LoadSuggestionsAsync();
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
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

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
                ErrorMessage = "서버에는 연결되었지만 로그인이 필요합니다.\n설정에서 사용자 ID와 비밀번호로 로그인하세요.";
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

        var record = new ChatSessionRecord
        {
            Id = _session.Id,
            Title = _chatHistory.BuildTitle(messages),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            WorkspaceRoot = WorkspaceRoot,
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
                    // 토큰 없음/만료 → "오류"가 아니라 "로그인 필요" 상태로 안내(다시 시도 무한반복 방지).
                    ApplyReadiness(ServerReadiness.Unauthenticated);
                    Transcript.Add(new SystemNoticeViewModel { Text = "로그인이 필요합니다. 설정에서 로그인하세요." });
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
