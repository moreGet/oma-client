using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FontStyle = System.Drawing.FontStyle;
using System.Windows.Forms;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using OhMyAgent.AiAgent.Client.ViewModels;
using OhMyAgent.AiAgent.Client.Views;
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;

namespace OhMyAgent.AiAgent.Client;

public partial class App : Application
{
    public static bool IsExiting { get; private set; }

    private MainWindow?               _mainWindow;
    private NotifyIcon?               _trayIcon;
    private HttpClient?               _httpClient;
    private ISettingsService?         _settingsService;
    private IGlobalHotkeyService?     _globalHotkey;
    private ITrayNotificationService? _trayNotification;
    private IChatWindowCoordinator?   _windowCoordinator;
    private AgentSessionViewModel?    _mainVm;
    private IAgentApiClient?          _api;
    private IWorkspaceHistoryService? _workspaceHistory;
    internal ISettingsService SettingsService => _settingsService!;
    internal IAgentApiClient? Api => _api;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1) Settings 먼저 로드 — 모두가 이를 읽는다.
        _settingsService = new SettingsService();
        await _settingsService.LoadAsync();

        // 2) Infra (BaseAddress 는 로드된 설정에서)
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_settingsService.Current.ServerBaseUrl),
            Timeout     = Timeout.InfiniteTimeSpan
        };

        // 3) Workspace 샌드박스
        var workspace = new WorkspaceContext(_settingsService);

        // 4) 스크립트 실행기 (run_command 엔진)
        var scriptExec = new ScriptExecutor();

        // 5) 11개 MVP 도구 (배열 순서 = 표시 순서)
        var tools = new ITool[]
        {
            new RunCommandTool(scriptExec),
            new ReadFileTool(),
            new WriteFileTool(),
            new EditFileTool(),
            new ListDirectoryTool(),
            new GlobTool(),
            new GrepTool(),
            new CreateDirectoryTool(),
            new MoveTool(),
            new CopyTool(),
            new DeleteTool(),
        };

        // 6) 도구 레지스트리
        var registry = new ToolRegistry(tools);

        // 7) 권한 게이트
        var permissions = new PermissionService(_settingsService);

        // 8) API 클라이언트
        _api = new AgentApiClient(_httpClient, _settingsService);

        // 8b) 워크스페이스 히스토리 (settings 기반, Phase D — B)
        var workspaceHistory = new WorkspaceHistoryService(_settingsService);
        _workspaceHistory = workspaceHistory;

        // 9) 오케스트레이터 (에이전트 루프)
        var orchestrator = new AgentOrchestrator(_api, registry, permissions, workspace, _settingsService);

        // 9b) 채팅 히스토리 / 첨부 / 제안 (Phase D — C, D, G)
        var chatHistory = new ChatHistoryService();
        var attachments = new FileAttachmentService();
        var suggestions = new StubSuggestionService();

        // 10) 루트 ViewModel
        _mainVm = new AgentSessionViewModel(
            orchestrator, _api, permissions, workspace, _settingsService,
            workspaceHistory, chatHistory, attachments, suggestions);

        // 11) Main Window
        _mainWindow = new MainWindow(_mainVm);
        MainWindow  = _mainWindow;

        // 12) Tray icon + Notification
        InitializeTrayIcon();
        _trayNotification = new TrayNotificationService(_trayIcon!);

        // 13) Window Coordinator
        _windowCoordinator = new ChatWindowCoordinator(
            () => _mainWindow!,
            () => _mainVm!,
            _trayNotification);

        // 14) Global Hotkey 서비스 생성 (HWND는 SourceInitialized 후 획득)
        _globalHotkey = new GlobalHotkeyService();
        _globalHotkey.HotkeyPressed += (_, _) =>
            Dispatcher.Invoke(_windowCoordinator.ToggleChatOnly);

        // 15) 설정 변경 시 workspace 동기화 + 워크스페이스 히스토리 자동 기록 + 핫키 재등록
        _settingsService.SettingsChanged += (_, s) =>
        {
            workspace.SetRoot(s.WorkspaceRoot);
            if (!string.IsNullOrWhiteSpace(s.WorkspaceRoot))
                // AddAsync는 RaiseSettingsChanged를 호출하지 않으므로 이 핸들러로 재진입하지 않는다.
                _ = workspaceHistory.AddAsync(s.WorkspaceRoot);
            _globalHotkey!.Unregister();
            _globalHotkey.Register(s.Hotkey);
        };

        // 16) 표시 + 초기화
        _mainWindow.Show();
        _ = _mainVm.InitializeAsync();
    }

    /// MainWindow의 HWND가 확보된 시점에 호출. 글로벌 핫키 후크를 건다.
    public void RegisterMainWindowHwnd(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        _globalHotkey!.Initialize(hwnd);
        _globalHotkey.Register(_settingsService!.Current.Hotkey);
    }

    /// 창을 트레이로 숨기고 풍선 힌트를 표시한다.
    public void HideToTray(Window window)
    {
        window.Hide();
        _trayNotification?.ShowHideHint(_settingsService?.Current.Hotkey.ToDisplayString() ?? "Ctrl+Space");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _globalHotkey?.Dispose();
        _trayIcon?.Dispose();
        _httpClient?.Dispose();
        base.OnExit(e);
    }

    // ── 시스템 트레이 ────────────────────────────────────────────────

    private void InitializeTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text    = "OhMyAgent — AI Agent Client",
            Icon    = CreateAppIcon(),
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("Show");
        showItem.Click += (_, _) => ShowMainWindow();

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) =>
        {
            if (_settingsService == null || _api == null) return;
            var settingsVm     = new SettingsViewModel(_settingsService, _api);
            var settingsWindow = new SettingsWindow(settingsVm);
            settingsWindow.Show();
            settingsWindow.Activate();
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowMainWindow()
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
    }

    internal void ExitApplication()
    {
        IsExiting = true;
        _globalHotkey?.Dispose();
        _trayIcon?.Dispose();
        Shutdown();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon CreateAppIcon()
    {
        using var bmp  = new Bitmap(32, 32);
        using var g    = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0x38, 0x8B, 0xFD));
        using var font  = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        g.DrawString("A", font, brush, new PointF(5f, 4f));
        var hIcon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }
}
