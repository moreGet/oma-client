using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FontStyle = System.Drawing.FontStyle;
using System.Windows.Forms;
using OhMyAgent.AiAgent.Client.Services;
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
    private MainViewModel?            _mainVm;
    private IRemoteAgentService?      _mcpService;
    internal ISettingsService SettingsService => _settingsService!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1) Infra
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
            Timeout     = Timeout.InfiniteTimeSpan
        };

        // 2) Settings 먼저 로드
        _settingsService = new SettingsService();
        await _settingsService.LoadAsync();

        // 3) Domain services
        var chatService        = new ChatService(_httpClient);
        var agentActionService = new AgentActionService();

        // 4) MCP 서비스 생성
        _mcpService = new McpRemoteAgentService(
            settings:  _settingsService!,
            sseServer: new McpSseServer(),
            executor:  new ScriptExecutor());

        // 4-1) ViewModel (MCP 포함)
        _mainVm = new MainViewModel(chatService, agentActionService, _settingsService, _mcpService);

        // 5) Main Window
        _mainWindow = new MainWindow(_mainVm);
        MainWindow  = _mainWindow;

        // 6) Tray icon + Notification
        InitializeTrayIcon();
        _trayNotification = new TrayNotificationService(_trayIcon!);

        // 7) Window Coordinator
        _windowCoordinator = new ChatWindowCoordinator(
            () => _mainWindow!,
            () => _mainVm!,
            _trayNotification);

        // 8) Global Hotkey 서비스 생성 (HWND는 SourceInitialized 후 획득)
        _globalHotkey = new GlobalHotkeyService();
        _globalHotkey.HotkeyPressed += (_, _) =>
            Dispatcher.Invoke(_windowCoordinator.ToggleChatOnly);

        // 9) 설정 변경 시 핫키 재등록
        _settingsService.SettingsChanged += (_, s) =>
        {
            _globalHotkey!.Unregister();
            _globalHotkey.Register(s.Hotkey);
        };

        _mainWindow.Show();
        _ = _mainVm.InitializeAsync();

        // MCP 서버 시작
        if (_settingsService!.Current.McpEnabled)
            _ = _mcpService.StartAsync();
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

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_mcpService != null)
            {
                await _mcpService.StopAsync();
                await _mcpService.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP shutdown error: {ex}");
        }
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
            if (_settingsService == null) return;
            var settingsVm     = new SettingsViewModel(_settingsService);
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
