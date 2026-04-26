using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Client.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IChatService        _chatService;
    private readonly IAgentActionService _agentActionService;
    private readonly ISettingsService    _settingsService;
    private readonly IRemoteAgentService? _mcpService;
    private readonly string              _sessionId = Guid.NewGuid().ToString();

    // ── 상태 프로퍼티 ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _hasError;
    [ObservableProperty] private string _errorMessage  = string.Empty;
    [ObservableProperty] private string _statusText    = "연결 중...";
    [ObservableProperty] private DomainOptions? _selectedDomain;

    [ObservableProperty] private double _windowOpacity = 1.0;

    partial void OnWindowOpacityChanged(double value)
        => _ = _settingsService.UpdateOpacityAsync(Math.Clamp(value, 0.3, 1.0));

    // ── MCP 서버 상태 ──────────────────────────────────────────────────

    [ObservableProperty] private bool   _isMcpRunning;
    [ObservableProperty] private string _mcpStatusText = "MCP 서버 비활성";

    // ── 컬렉션 ─────────────────────────────────────────────────────────

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public ObservableCollection<DomainOptions> Domains { get; } =
    [
        new("general",       "일반"),
        new("ontology",      "온톨로지"),
        new("code",          "코드 분석"),
        new("knowledge",     "지식 그래프"),
    ];

    // ── 생성자 ─────────────────────────────────────────────────────────

    public MainViewModel(IChatService chatService, IAgentActionService agentActionService,
                         ISettingsService settingsService, IRemoteAgentService? mcpService = null)
    {
        _chatService        = chatService;
        _agentActionService = agentActionService;
        _settingsService    = settingsService;
        _mcpService         = mcpService;
        SelectedDomain      = Domains[0];
        _windowOpacity      = settingsService.Current.Opacity;

        // MCP 상태 구독
        if (_mcpService != null)
        {
            _mcpService.RunningStateChanged += OnMcpRunningStateChanged;
            _isMcpRunning  = _mcpService.IsRunning;
            _mcpStatusText = GetMcpStatusText(_mcpService.IsRunning, _mcpService.Port);
        }
    }

    // ── 초기화 ─────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await CheckConnectionAsync();
        if (IsConnected)
            AddSystemMessage("에이전트 서버에 연결되었습니다. 무엇을 도와드릴까요?");
    }

    // ── 커맨드: 메시지 전송 ────────────────────────────────────────────

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken ct)
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        Messages.Add(new ChatMessageViewModel { Content = text, IsUser = true });

        var request = new UserMessagesDto(_sessionId, text, SelectedDomain?.Id ?? "general");
        InputText  = string.Empty;
        IsBusy     = true;
        HasError   = false;

        var aiMsg = new ChatMessageViewModel { IsUser = false, IsStreaming = true };
        Messages.Add(aiMsg);

        try
        {
            var sb             = new StringBuilder();
            var fileCreated    = false;

            await foreach (var chunk in _chatService.StreamResponseAsync(request, ct))
            {
                sb.Append(chunk);
                aiMsg.Content = sb.ToString();

                // [ACTION:CREATE_FILE] 트리거 감지 (1회만 실행)
                if (!fileCreated && sb.ToString().Contains("[ACTION:CREATE_FILE]", StringComparison.Ordinal))
                {
                    fileCreated = true;
                    _ = ExecuteFileActionAsync(sb.ToString(), ct);
                }
            }

            aiMsg.IsStreaming = false;
        }
        catch (OperationCanceledException)
        {
            aiMsg.Content    += "\n[취소됨]";
            aiMsg.IsStreaming  = false;
        }
        catch (AgentException ex)
        {
            Messages.Remove(aiMsg);
            ErrorMessage = ex.Message;
            HasError     = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 커맨드: 연결 재시도 ────────────────────────────────────────────

    [RelayCommand]
    private async Task RetryConnectionAsync()
    {
        HasError = false;
        await CheckConnectionAsync();
        if (IsConnected)
            AddSystemMessage("서버 연결이 복구되었습니다.");
    }

    // ── 커맨드: 채팅 초기화 ────────────────────────────────────────────

    [RelayCommand]
    private void ClearMessages() => Messages.Clear();

    // ── 내부 메서드 ────────────────────────────────────────────────────

    private async Task CheckConnectionAsync()
    {
        StatusText   = "연결 중...";
        IsConnected  = await _chatService.CheckConnectionAsync();
        StatusText   = IsConnected ? "Connected" : "Disconnected";

        if (!IsConnected)
        {
            HasError     = true;
            ErrorMessage = "에이전트 서버(localhost:8080)에 연결할 수 없습니다.\n서버가 실행 중인지 확인하세요.";
        }
    }

    private async Task ExecuteFileActionAsync(string content, CancellationToken ct)
    {
        try
        {
            var path = await _agentActionService.ExecuteCreateFileAsync(content, ct);
            AddSystemMessage($"📄 파일이 생성되었습니다: {path}");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"⚠️ 파일 생성 실패: {ex.Message}");
        }
    }

    private void AddSystemMessage(string text) =>
        Messages.Add(new ChatMessageViewModel { Content = text, IsUser = false });

    // ── MCP 이벤트 핸들러 ──────────────────────────────────────────────

    private void OnMcpRunningStateChanged(object? sender, bool isRunning)
    {
        IsMcpRunning  = isRunning;
        McpStatusText = GetMcpStatusText(isRunning, _mcpService?.Port ?? 0);
    }

    private static string GetMcpStatusText(bool isRunning, int port)
        => isRunning ? $"MCP :{port}" : "MCP 오프";
}
