using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FontStyle = System.Drawing.FontStyle;
using System.Windows.Forms;
using OhMyAgent.AiAgent.Client.Models;
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
    private HttpClient?               _toolHttpClient;
    private ISettingsService?         _settingsService;
    private IGlobalHotkeyService?     _globalHotkey;
    private ITrayNotificationService? _trayNotification;
    private IChatWindowCoordinator?   _windowCoordinator;
    private AgentSessionViewModel?    _mainVm;
    private IAgentApiClient?          _api;
    private IProjectService?          _projectService;
    private IBinaryIntegrityService?  _binaryIntegrity;
    internal ISettingsService SettingsService => _settingsService!;
    internal IAgentApiClient? Api => _api;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 트레이 상주 앱 — 창을 닫아도(트레이로 숨김) 종료되지 않게. 종료는 ExitApplication() 단일 경로.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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

        // 4b) http_fetch 전용 HttpClient (앱 API용 _httpClient 와 분리된 인스턴스)
        _toolHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // 5) 파일/셸 도구 + 시스템(환경/클립보드/프로세스/HTTP/스크린샷) 도구 묶음 (배열 순서 = 표시 순서)
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
            // ── 시스템 경량 도구 묶음 ──
            new GetEnvironmentTool(),
            new ClipboardReadTool(),
            new ClipboardWriteTool(),
            new ListProcessesTool(),
            new ListProcessesMemoryKbTool(),
            new StartProcessTool(),
            new KillProcessTool(),
            new HttpFetchTool(_toolHttpClient),
            new ScreenshotTool(),
        };

        // 6) 도구 레지스트리
        var registry = new ToolRegistry(tools);

        // 7) 권한 게이트
        var permissions = new PermissionService(_settingsService);

        // 8) API 클라이언트
        _api = new AgentApiClient(_httpClient, _settingsService);

        // 8b) 워크스페이스 히스토리 (settings 기반, Phase D — B)
        var workspaceHistory = new WorkspaceHistoryService(_settingsService);

        // 9) 오케스트레이터 (에이전트 루프)
        var orchestrator = new AgentOrchestrator(_api, registry, permissions, workspace, _settingsService);

        // 9a) 바이너리 무결성 검사 서비스 (설치 디렉토리 SHA256 검증, Windows 전용)
        _binaryIntegrity = new BinaryIntegrityService(new AuthenticodeVerifier());

        // 9b) 채팅 히스토리 / 첨부 / 제안 (Phase D — C, D, G)
        var chatHistory = new ChatHistoryService();
        var attachments = new FileAttachmentService();
        var suggestions = new StubSuggestionService();

        // 9c) 프로젝트(대화 컨테이너) 로컬 영속 + 선택적 서버 동기화 (v5 — #4)
        //     ProjectsViewModel 조립은 ViewModel 에이전트가 추가한다.
        _projectService = new ProjectService(chatHistory, _api);

        // 10) 루트 ViewModel
        _mainVm = new AgentSessionViewModel(
            orchestrator, _api, permissions, workspace, _settingsService,
            workspaceHistory, chatHistory, attachments, suggestions);

        // 10b) 프로젝트 사이드바 VM 조립·주입 (#4). 메인 DataContext에서 Projects.* 로 바인딩.
        _mainVm.Projects = new ProjectsViewModel(_projectService, chatHistory);

        // 10a) 배너의 "로그인" 요청 → 로그인 게이트를 다시 연다(설정창엔 더 이상 로그인이 없음).
        _mainVm.LoginRequested += (_, _) => ReopenLogin();

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
            // v5: 다중 루트 동기화 — 활성(Enabled) 폴더 전체를 워크스페이스에 반영. 비면 주 루트 폴백.
            var activeRoots = s.Workspaces
                .Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Path))
                .Select(w => w.Path)
                .ToList();
            if (activeRoots.Count > 0)
                workspace.SetRoots(activeRoots);
            else
                workspace.SetRoot(s.WorkspaceRoot);
            if (!string.IsNullOrWhiteSpace(s.WorkspaceRoot))
                // AddAsync는 RaiseSettingsChanged를 호출하지 않으므로 이 핸들러로 재진입하지 않는다.
                _ = workspaceHistory.AddAsync(s.WorkspaceRoot);
            _globalHotkey!.Unregister();
            _globalHotkey.Register(s.Hotkey);
        };

        // 16) 로그인 게이트 — 인증돼 있으면 바로 메인, 아니면 로그인 랜딩부터.
        var readiness = await _api.CheckReadinessAsync();
        if (readiness == ServerReadiness.Ready)
            ShowMainWindow(initialize: true);
        else
            ShowLoginLanding();
    }

    /// 메인 윈도우를 표시하고(최초 1회) 초기화한다.
    private void ShowMainWindow(bool initialize)
    {
        _mainWindow!.Show();
        _mainWindow.Activate();
        if (initialize)
            _ = _mainVm!.InitializeAsync();
    }

    /// 첫 랜딩 = 로그인 페이지. 로그인 성공 시에만 메인으로 진입, 미로그인 종료 시 앱 종료.
    private void ShowLoginLanding()
    {
        var loginVm = new LoginViewModel(_api!, _settingsService!);
        var login   = new LoginWindow(loginVm);
        var success = false;

        loginVm.Succeeded += (_, _) =>
        {
            success = true;
            ShowMainWindow(initialize: true);   // 창 공백 없이 메인 먼저 띄우고
            login.Close();                      // 그다음 로그인 닫기
        };
        login.Closed += (_, _) =>
        {
            if (!success && !IsExiting)
                ExitApplication();              // 로그인 없이 닫으면 메인 진입 차단 → 종료
        };

        login.Show();
        login.Activate();
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
        IsExiting = true;
        DisposeAll();
        base.OnExit(e);

        // 어떤 비백그라운드 스레드/후크가 남아도 프로세스를 확실히 종료(좀비 방지).
        Environment.Exit(e.ApplicationExitCode);
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
        settingsItem.Click += (_, _) => OpenSettingsWindow();

        var integrityItem = new ToolStripMenuItem("무결성 검사");
        integrityItem.Click += (_, _) =>
        {
            if (_binaryIntegrity == null) return;
            // IntegrityViewModel은 UI 스레드에서 생성(Progress<T> 마샬링 캡처).
            var integrityVm     = new IntegrityViewModel(_binaryIntegrity);
            var integrityWindow = new IntegrityWindow(integrityVm);
            integrityWindow.Show();
            integrityWindow.Activate();
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(integrityItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowMainWindow()
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
    }

    /// 설정창을 연다(트레이 메뉴 + 배너 로그인 공용). 닫히면 연결/인증 상태를 재점검해 배너를 갱신한다.
    /// 세션 중 401(로그인 필요) 발생 시 로그인 게이트를 모달로 다시 연다.
    /// 시작 게이트(ShowLoginLanding)와 달리 닫아도 앱을 종료하지 않는다 — 성공 시 연결만 재점검.
    private void ReopenLogin()
    {
        if (_api == null || _settingsService == null) return;

        var loginVm = new LoginViewModel(_api, _settingsService);
        var login   = new LoginWindow(loginVm) { Owner = _mainWindow };

        loginVm.Succeeded += (_, _) =>
        {
            login.Close();
            if (_mainVm is { } vm)
                _ = vm.RetryConnectionCommand.ExecuteAsync(null);
        };

        login.ShowDialog();
    }

    private void OpenSettingsWindow()
    {
        if (_settingsService == null || _api == null) return;

        var settingsVm     = new SettingsViewModel(_settingsService, _api);
        _ = settingsVm.InitializeAsync();
        var settingsWindow = new SettingsWindow(settingsVm) { Owner = _mainWindow };
        settingsWindow.Closed += (_, _) =>
        {
            if (_mainVm is { } vm)
                _ = vm.RetryConnectionCommand.ExecuteAsync(null);
        };
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    /// 모든 종료 신호(메인 X · 트레이 Exit · 미로그인 종료)의 단일 진입점.
    /// 자원을 정리하고 프로세스를 완전히 종료한다(좀비 방지).
    internal void ExitApplication()
    {
        if (IsExiting) return;   // 재진입 방지
        IsExiting = true;
        DisposeAll();
        Shutdown();
    }

    /// 모든 백그라운드 자원/후크/트레이 아이콘 정리. 여러 번 호출돼도 안전.
    private void DisposeAll()
    {
        try { _globalHotkey?.Dispose(); } catch { /* ignore */ }
        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;   // 트레이 고스트 아이콘 방지
                _trayIcon.Dispose();
            }
        }
        catch { /* ignore */ }
        try { _httpClient?.Dispose(); }     catch { /* ignore */ }
        try { _toolHttpClient?.Dispose(); } catch { /* ignore */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g   = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 라운드 그라데이션 배경 (보라 → 블루)
        using (var bgPath = RoundedRectPath(new RectangleF(0, 0, 31, 31), 8f))
        using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new RectangleF(0, 0, 32, 32),
                   Color.FromArgb(0x7C, 0x5C, 0xFF), Color.FromArgb(0x38, 0x8B, 0xFD), 55f))
            g.FillPath(bg, bgPath);

        // 화이트 스파클 심볼
        using (var sparkle = SparklePath(16f, 16f, 11f))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, sparkle);

        var hIcon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    /// 4-포인트 스파클(반짝임) 경로 — 트레이/브랜드 심볼.
    private static System.Drawing.Drawing2D.GraphicsPath SparklePath(float cx, float cy, float r)
    {
        var ri = r * 0.34f;
        var d  = ri * 0.7071f;
        var pts = new[]
        {
            new PointF(cx, cy - r), new PointF(cx + d, cy - d),
            new PointF(cx + r, cy), new PointF(cx + d, cy + d),
            new PointF(cx, cy + r), new PointF(cx - d, cy + d),
            new PointF(cx - r, cy), new PointF(cx - d, cy - d),
        };
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddClosedCurve(pts, 0.25f);
        return p;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(RectangleF r, float radius)
    {
        var d = radius * 2f;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
