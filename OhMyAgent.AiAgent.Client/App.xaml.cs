using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Forms;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Chat;
using OhMyAgent.AiAgent.Client.Services.Tools;
using OhMyAgent.AiAgent.Client.ViewModels;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using OhMyAgent.AiAgent.Client.Views;
using OhMyAgent.AiAgent.Client.Views.Chat;
using Application = System.Windows.Application;

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
    private IToolPolicyService?       _toolPolicy;
    private bool                      _loginShowing;
    private IProjectService?          _projectService;
    private IBinaryIntegrityService?  _binaryIntegrity;

    // ── 실시간 메신저(사람↔사람) — LLM 채팅과 별개 모듈 ──
    private IChatRealtimeService?      _chatRealtime;
    private ChatMessengerViewModel?   _chatMessengerVm;
    private IChatMessengerCoordinator? _chatCoordinator;
    private bool                      _chatStarted;   // 최초 표시 시 1회 StartCommand 가드

    internal ISettingsService SettingsService => _settingsService!;
    internal IAgentApiClient? Api => _api;
    /// <summary>MainWindow 사이드바 배지 중계용 — 메신저 VM 노출(없으면 null).</summary>
    internal ChatMessengerViewModel? ChatMessengerVm => _chatMessengerVm;

    // async void — 미관측 예외 시 조용한 시작 크래시를 막도록 본문을 감싸 최상위에서 방어한다.
    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            await StartupAsync(e);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"앱을 시작하지 못했습니다:\n\n{ex.Message}",
                "OhMyAgent", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task StartupAsync(StartupEventArgs e)
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

        // 4c) 작업 계획(todo) 공유 저장소 — manage_todos 도구가 쓰고 메인 VM이 구독해 화면에 반영(싱글톤 1개).
        var todoService = new TodoService();

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
            // ── 에이전트 메타 도구 (다단계 작업 계획 추적) ──
            new ManageTodosTool(todoService),
            // ── 사무직 문서·데이터 도구 묶음 (CSV: BCL / Excel: ClosedXML / PDF: PdfPig / Word: BCL) ──
            new ReadCsvTool(),
            new WriteCsvTool(),
            new ReadExcelTool(),
            new WriteExcelTool(),
            new ReadPdfTool(),
            new ReadDocumentTool(),
            // ── 압축 도구 (zip) ──
            new CompressFilesTool(),
            new ExtractArchiveTool(),
        };

        // 6) 도구 레지스트리
        var registry = new ToolRegistry(tools);

        // 7) 권한 게이트
        var permissions = new PermissionService(_settingsService);

        // 8) API 클라이언트
        _api = new AgentApiClient(_httpClient, _settingsService);

        // 8a) 서버 도구 정책 게이트(싱글톤 1개) — 오케스트레이터·VM가 동일 인스턴스를 공유한다.
        _toolPolicy = new ToolPolicyService(_api);

        // 9) 오케스트레이터 (에이전트 루프) — 정책 게이트를 가장 앞단에 끼운다.
        var orchestrator = new AgentOrchestrator(_api, registry, permissions, workspace, _settingsService, _toolPolicy);

        // 9a) 바이너리 무결성 검사 서비스 (설치 디렉토리 SHA256 검증, Windows 전용)
        _binaryIntegrity = new BinaryIntegrityService(new AuthenticodeVerifier());

        // 9b) 채팅 히스토리 / 첨부 / 제안 (Phase D — C, D, G)
        var chatHistory = new ChatHistoryService();
        var attachments = new FileAttachmentService();
        var suggestions = new StubSuggestionService();

        // 9c) 프로젝트(대화 컨테이너) 로컬 영속 + 선택적 서버 동기화 (v5 — #4)
        //     ProjectsViewModel 조립은 ViewModel 에이전트가 추가한다.
        _projectService = new ProjectService(chatHistory, _api);

        // 9d) 대화 세션 서버 동기화(여러 PC 공유) — 싱글톤 1개. 로컬(chatHistory)↔원격(_api) 브릿지.
        var sessionSync = new SessionSyncService(_api, chatHistory);

        // 10) 루트 ViewModel
        _mainVm = new AgentSessionViewModel(
            orchestrator, _api, permissions, workspace, _settingsService,
            chatHistory, attachments, suggestions, _toolPolicy, sessionSync, todoService);

        // 10b) 프로젝트 사이드바 VM 조립·주입 (#4). 메인 DataContext에서 Projects.* 로 바인딩.
        _mainVm.Projects = new ProjectsViewModel(_projectService, chatHistory);

        // 10a) 로그인 필요(세션 만료/401) → 모든 창을 닫고 로그인 화면만 남긴다(통합 경로).
        _mainVm.LoginRequested += (_, _) => ReturnToLogin();

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

        // 13b) 실시간 메신저(사람↔사람) 조립 — LLM 채팅(ChatWindowCoordinator)과 완전 별개 모듈(설계서 §6).
        //      currentUserId = JWT(sub) = member UUID. /users/me 엔 id 없음 → 토큰 디코드가 유일 소스.
        var chatApi   = new ChatApiClient(_httpClient!, _settingsService!);
        var chatSocket = new ChatSocketClient(_settingsService!);
        _chatRealtime = new ChatRealtimeService(chatApi, chatSocket, _settingsService!);

        var currentUserId = JwtIdentity.MemberId(_settingsService!.Current.AuthToken);
        _chatMessengerVm = new ChatMessengerViewModel(_chatRealtime, currentUserId);
        _chatMessengerVm.LoginRequested += (_, _) => ReturnToLogin();

        // 창은 1회 생성·재사용(coordinator 내부 lazy 캐시). 팩토리는 1회만 호출되도록 보정 캐시.
        ChatMessengerWindow? chatWindow = null;
        _chatCoordinator = new ChatMessengerCoordinator(
            () => chatWindow ??= new ChatMessengerWindow(_chatMessengerVm!),
            _trayNotification!);

        // 14) Global Hotkey 서비스 생성 (HWND는 SourceInitialized 후 획득)
        _globalHotkey = new GlobalHotkeyService();
        _globalHotkey.HotkeyPressed += (_, _) =>
            Dispatcher.Invoke(_windowCoordinator.ToggleChatOnly);

        // 15) 설정 변경 시 workspace(다중 루트) 동기화 + 핫키 재등록
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
        if (_loginShowing) return;   // 이미 로그인 화면이면 중복 생성 금지.
        _loginShowing = true;

        var loginVm = new LoginViewModel(_api!, _settingsService!);
        var login   = new LoginWindow(loginVm);
        var success = false;

        loginVm.Succeeded += async (_, _) =>
        {
            // 로그인 POST 성공 → 토큰이 보호 엔드포인트에서도 유효한지(readiness) 확인 후 메인 진입.
            // 이렇게 해야 "로그인은 되는데 서버가 거부"하는 경우 무한 로그인 루프를 막는다.
            ServerReadiness readiness;
            try { readiness = await _api!.CheckReadinessAsync(); }
            catch { readiness = ServerReadiness.Ready; }

            if (readiness == ServerReadiness.Unauthenticated)
            {
                loginVm.HasError = true;
                loginVm.StatusMessage = "로그인은 됐지만 서버 인증에 실패했습니다. 잠시 후 다시 시도하세요.";
                return;   // 로그인 창 유지 (루프 방지)
            }

            success = true;
            ShowMainWindow(initialize: true);   // 메인 먼저 띄우고
            login.Close();                      // 그다음 로그인 닫기
        };
        login.Closed += (_, _) =>
        {
            _loginShowing = false;
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

        var messengerItem = new ToolStripMenuItem("메신저");
        messengerItem.Click += (_, _) => ToggleMessenger();

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
        menu.Items.Add(messengerItem);
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

    private void OpenSettingsWindow()
    {
        if (_settingsService == null || _api == null) return;

        var settingsVm     = new SettingsViewModel(_settingsService, _api);
        settingsVm.LoggedOut += (_, _) => ReturnToLogin();
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

    /// 메신저 창 토글(트레이/사이드바 버튼 진입점). 최초 표시 시 1회 StartCommand 로 WS connect + unread 로드.
    internal void ToggleMessenger()
    {
        if (_chatCoordinator is null) return;
        StartMessengerIfNeeded();
        _chatCoordinator.Toggle();
    }

    /// 메신저 최초 표시 시 1회만 StartCommand 실행(WS connect + unread + 방목록). 재로그인 시 가드 리셋됨.
    private void StartMessengerIfNeeded()
    {
        if (_chatStarted || _chatMessengerVm is null) return;
        _chatStarted = true;
        _ = _chatMessengerVm.StartCommand.ExecuteAsync(null);
    }

    /// 로그인이 필요한 모든 상황(로그아웃 · 세션 만료/401)의 단일 진입점.
    /// 실행 중 작업·세션을 강제 종료하고, 모든 보조 창을 닫고 메인은 숨긴 뒤 로그인 화면만 남긴다.
    /// 로그인 성공 시 메인 복귀, 그냥 닫으면 앱 종료(시작 게이트와 동일).
    internal void ReturnToLogin()
    {
        if (IsExiting) return;
        if (_loginShowing) return;   // 이미 로그인 화면이면 무시(중복/루프 방지).

        // 1) 메인 세션/화면 상태 초기화 + 실행 중 에이전트 취소.
        _mainVm?.PrepareForLogout();

        // 1b) 메신저 WS 정리 — 재로그인 시 토큰이 바뀌므로 stale 연결을 끊고 Start 가드를 리셋한다.
        //     IsMine 판정은 서버 sender_id 기준이라 currentUserId(VM) 갱신 지연의 실무 영향은 최소.
        _chatStarted = false;
        if (_chatRealtime is { } realtime)
            _ = realtime.StopAsync();

        // 2) 보조 창(설정·무결성·채팅전용 등)은 닫고, 메인은 숨긴다(재로그인 시 재사용).
        foreach (var w in Windows.OfType<Window>().ToList())
        {
            if (w is LoginWindow) continue;
            if (ReferenceEquals(w, _mainWindow))
                w.Hide();
            else
                w.Close();
        }

        // 3) 로그인 화면만 남긴다.
        ShowLoginLanding();
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
        // 메신저 정리(WS stop + VM 구독해제 + 비동기 dispose). fire-and-forget 안전(종료 경로).
        try { _ = _chatRealtime?.StopAsync(); }   catch { /* ignore */ }
        try { _chatMessengerVm?.Dispose(); }      catch { /* ignore */ }
        try { _ = _chatRealtime?.DisposeAsync(); } catch { /* ignore */ }

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
