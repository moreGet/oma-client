using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    private bool _isBusy;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _statusText = "연결 중...";
    [ObservableProperty] private string _workspaceRoot = string.Empty;
    [ObservableProperty] private PermissionMode _currentPermissionMode = PermissionMode.Manual;
    [ObservableProperty] private double _windowOpacity = 1.0;
    [ObservableProperty] private ApprovalRequestViewModel? _pendingApproval;
    [ObservableProperty] private string _lastUsageText = string.Empty;

    // ── Collections ────────────────────────────────────────────────────

    public ObservableCollection<ITranscriptItem> Transcript { get; } = [];

    /// <summary>Selectable permission modes for the header selector.</summary>
    public IReadOnlyList<PermissionMode> PermissionModes { get; } =
        [PermissionMode.Manual, PermissionMode.AutoSafe, PermissionMode.FullAuto];

    // ── Constructor ────────────────────────────────────────────────────

    public AgentSessionViewModel(
        IAgentOrchestrator orchestrator,
        IAgentApiClient api,
        IPermissionService permissions,
        IWorkspaceContext workspace,
        ISettingsService settings)
    {
        _orchestrator = orchestrator;
        _api = api;
        _permissions = permissions;
        _workspace = workspace;
        _settings = settings;

        // Surface Manual-mode approvals through the inline approval card.
        _permissions.SetApprovalHandler(RequestApprovalAsync);

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
        if (IsConnected)
            Transcript.Add(new SystemNoticeViewModel
            {
                Text = "에이전트 서버에 연결되었습니다. 무엇을 도와드릴까요?",
            });
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
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private async Task RetryConnectionAsync()
    {
        StatusText = "연결 중...";
        try
        {
            IsConnected = await _api.CheckHealthAsync().ConfigureAwait(false);
        }
        catch
        {
            IsConnected = false;
        }

        StatusText = IsConnected ? "Connected" : "Disconnected";
        if (!IsConnected)
        {
            HasError = true;
            ErrorMessage = $"에이전트 서버({_settings.Current.ServerBaseUrl})에 연결할 수 없습니다.\n서버가 실행 중인지 확인하세요.";
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Transcript.Clear();
        _toolCards.Clear();
        _currentAssistant = null;
        _session = new AgentSession();
        _permissions.ClearSessionRules();
        HasError = false;
        ErrorMessage = string.Empty;
        LastUsageText = string.Empty;
        StatusText = IsConnected ? "Connected" : "Disconnected";
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
                    LastUsageText = $"in:{usage.InputTokens} out:{usage.OutputTokens}";
                break;

            case AgentError err:
                HasError = true;
                ErrorMessage = err.Message;
                StatusText = "오류";
                Transcript.Add(new SystemNoticeViewModel { Text = $"오류 [{err.Code}]: {err.Message}" });
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
