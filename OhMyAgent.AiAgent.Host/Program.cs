using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Loop;
using OhMyAgent.AiAgent.Client.Services.Tools;
using OhMyAgent.AiAgent.Host;

AppLog.Initialize();
AppLog.Info("Host", $"헤드리스 시작 — v{AppVersion.Full}");

var cfg = HeadlessConfig.FromEnvironment();   // OHMYAGENT_SERVER_URL / _AUTH_TOKEN / _MODEL / _WORKSPACE / _HEADLESS_APPROVAL

// 취소 토큰을 가장 먼저 만든다 — 기동 선검사(8-a)가 네트워크를 타므로 그 구간도 Ctrl+C 로 끊을 수 있어야 한다.
using var cts = ConsoleCancellation();   // Ctrl+C / SIGTERM → 취소 토큰

// 0) 헤드리스 디스패처 — UI 스레드 없음. 즉시 실행.
IUiDispatcher ui = new ImmediateUiDispatcher();

// 1) 설정 — 파일(~/.config/OhMyAgent/settings.json) 로드 후 env 로 서버설정 오버라이드.
var settings = new SettingsService(ui);
await settings.LoadAsync();
// env 미지정 항목은 로드된 기존 값을 보존한다(재로그인 없이 이어서 실행 가능).
await settings.UpdateServerConfigAsync(
    string.IsNullOrWhiteSpace(cfg.ServerBaseUrl) ? settings.Current.ServerBaseUrl : cfg.ServerBaseUrl,
    "Bearer",
    string.IsNullOrWhiteSpace(cfg.AuthToken) ? settings.Current.AuthToken : cfg.AuthToken,
    string.IsNullOrWhiteSpace(cfg.ModelId) ? settings.Current.ModelId : cfg.ModelId,
    cfg.MaxIterations ?? settings.Current.MaxIterations);
if (!string.IsNullOrWhiteSpace(cfg.WorkspaceRoot))
    await settings.UpdateWorkspaceRootAsync(cfg.WorkspaceRoot!);

// 2) HTTP — App 과 동일하게 BaseAddress 미설정(요청마다 절대 URI). 전체 타임아웃은 무한(SSE 스트리밍)이되,
//    연결 단계만 별도 제한한다. 헤드리스는 취소해 줄 사용자가 없어, 블랙홀 서버(SYN 무응답)에 무한 대기하면
//    프로세스가 영구 행이다(WSL 스모크 테스트에서 실증). ConnectTimeout 은 수립된 스트림에는 영향 없다.
var httpClient = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
{
    Timeout = Timeout.InfiniteTimeSpan
};
var toolHttp   = HttpFetchTool.CreateDefaultClient();   // internal → Core IVT(Host)

// 3) 워크스페이스 샌드박스
var workspace = new WorkspaceContext(settings);

// 4) 스크립트 실행기(run_command 엔진) — OS 라우팅은 ScriptExecutor 내부.
var scriptExec = new ScriptExecutor();

// 5) 공유 서비스
var todoService = new TodoService();
// schedule_wakeup 도구 → 루프 엔진 사이의 우편함. 도구와 컨트롤러가 같은 인스턴스를 봐야
// "이번 턴에 모델이 정한 다음 시점"이 전달된다(조립 순서: sink → tool → controller).
var wakeupSink  = new WakeupSink();

// 5a) API 클라이언트 — 도구(generate_image)가 서버를 대리 호출자로 쓰므로 도구 조립보다 먼저 만든다.
//     설정과 HttpClient 만 필요해 이 위치에서 만들 수 있다(정책·컴팩터는 8) 에서 이 인스턴스를 재사용).
var api = new AgentApiClient(httpClient, settings);

// 6) headless-safe 도구 (Clipboard 2종 + Screenshot 제외)
var tools = HeadlessAgentHost.BuildHeadlessTools(scriptExec, toolHttp, todoService, wakeupSink, api);

// 7) 권한 게이트 — 핸들러는 정책에 따라 등록. 미등록이면 gated=Deny(안전 기본).
var permissions = new PermissionService(settings);
HeadlessPermissionPolicy.Apply(permissions, cfg.ApprovalMode);   // "deny"(기본) | "auto"

// 8) 정책/컴팩터 (api 는 5a 에서 이미 만들어졌다 — generate_image 도구가 그보다 먼저 필요했다)
var toolPolicy = new ToolPolicyService(api);
var compactor  = new ContextCompactor(api, settings);

// 8-a) 기동 선검사 — 토큰이 죽었으면 여기서 끝낸다.
//      헤드리스는 재로그인 UI 가 없어 401 을 만나도 스스로 회복하지 못한다. 그대로 진행하면 "살아있지만
//      아무 일도 못 하는" 좀비가 되고, 종료 코드가 0 이라 systemd 도 개입하지 못한다(운영에서 관측됨).
//      실패 사유를 종료 코드로 드러내 재시작·경보가 걸리게 한다.
switch (await api.CheckReadinessAsync(cts.Token))
{
    case ServerReadiness.Disconnected:
        AppLog.Error("Host", $"서버에 연결할 수 없습니다: {settings.Current.ServerBaseUrl}");
        Console.Error.WriteLine($"⚠ 서버에 연결할 수 없습니다: {settings.Current.ServerBaseUrl}");
        return HostExitCode.ServerUnreachable;

    case ServerReadiness.Unauthenticated:
        AppLog.Error("Host", "인증 토큰이 없거나 만료·무효합니다 — 기동을 중단합니다.");
        Console.Error.WriteLine("⚠ OHMYAGENT_AUTH_TOKEN 이 없거나 만료·무효합니다. 새 토큰으로 재시작하세요.");
        return HostExitCode.AuthFailure;
}

// 8-b) 서버 도구 정책 로드 — App 의 로그인 직후 시맨틱과 동일(AgentSessionViewModel 참조).
//      404(미구현) = 정책 미운영 확정 → fail-open. 그 외 실패 = fail-closed(전 도구 차단)로 계속 실행하되
//      이유를 알려준다. 이 호출이 없으면 정책 상태가 초기값(Unavailable)에 머물러 모든 도구가 영구 차단된다
//      (WSL 스모크 테스트에서 실증).
try { await toolPolicy.LoadAsync(); }
catch (Exception ex)
{
    AppLog.Warn("Host", $"도구 정책 로드 실패 — fail-closed 로 계속: {ex.Message}");
    Console.Error.WriteLine($"⚠ {ex.Message}");
}

// 8-b-2) 런타임 인증 실패 감시 — 401 이 연속 임계값에 닿으면 취소 토큰을 당겨 프로세스를 접는다.
//        토큰 갱신 경로가 없으므로(재로그인 UI 부재) 재시도는 무의미하고, 종료만이 systemd 에
//        "새 토큰으로 재시작하라"를 전달하는 유일한 수단이다.
var authFatal   = false;
var authFailures = new AuthFailureReporter(new AuthFailureMonitor(), () =>
{
    authFatal = true;
    try { cts.Cancel(); } catch (ObjectDisposedException) { /* 이미 종료 경로 */ }
});

// 8-c) 서버 추가 위험명령 패턴(디폴트에 가산) — 미구현/오류면 내장 블랙리스트만으로 방어.
try { SecurityValidator.SetServerPatterns(await api.GetCommandSecurityPolicyAsync()); }
catch { /* graceful — 내장 디폴트가 바닥을 방어한다. */ }

// 9) 서브에이전트(task) — 재귀 방지 위해 task 도구 없는 레지스트리로 조립.
//    subagentTools 는 이 시점의 tools(기본 도구)에서만 뽑으므로 discover/ask_agent 는 자동 제외된다
//    (ask_agent 서브에이전트 팬아웃 봉쇄 — 스펙 §10).
var subagentTools   = tools.Where(t => TaskTool.AllowedToolNames.Contains(t.Name)).ToArray();
var subOrchestrator = new AgentOrchestrator(
    api, new ToolRegistry(subagentTools), permissions, workspace, settings, toolPolicy, compactor,
    suppressThinking: true);

// 9-b) 레지스트리 협업 계층 — 등록 클라(컨트롤플레인) + A2A 채팅 클라(SSE 전용) + 협업 도구 2종.
//      brokerKeyStore 는 도구(exclude_self)·인증기·lifecycle 가 공유하는 슬롯(LISTEN 모드에서 채워짐).
var brokerKeyStore = new BrokerKeyStore();
var registryClient = new AgentRegistryClient(httpClient, settings);
var a2aHttp        = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
{
    Timeout = Timeout.InfiniteTimeSpan   // A2A SSE 스트리밍 — 전체 타임아웃 무한(연결 단계만 제한).
};
var a2aChat        = new A2aChatClient(a2aHttp);
var discoverTool   = new DiscoverAgentsTool(registryClient, brokerKeyStore);
var askAgentTool   = new AskAgentTool(registryClient, a2aChat, brokerKeyStore);

// 10) 메인 레지스트리 = 기본 도구 + task + 협업 도구 2종
var registry = new ToolRegistry([.. tools, new TaskTool(subOrchestrator, workspace), discoverTool, askAgentTool]);

// 11) 오케스트레이터
var orchestrator = new AgentOrchestrator(api, registry, permissions, workspace, settings, toolPolicy, compactor);

// 12) 실행 코어 + 모드 분기
// using — A2A 리스너 경로처럼 중간에 return 하는 분기에서도 루프 Task 가 확실히 접히게 한다.
using var loop = new LoopController(wakeupSink);
var host = new HeadlessAgentHost(orchestrator, authFailures, loop);

// 12-a) A2A 서버 모드 — OHMYAGENT_LISTEN 이 있으면 stdin 루프 대신 HttpListener 로 수신(스펙 §1-G).
//       기존 원샷/대화 경로는 LISTEN 미설정 시 완전 불변.
var a2a = A2aOptions.FromEnvironment(settings.Current.ModelId);   // Mode(broker/token/anon) 포함
if (a2a.IsListenMode)
{
    a2a.ValidateOrThrow();   // token 모드 토큰 부재 + ANON≠1 → 기동 거부(§1-E)

    var regOpts  = AgentRegistryOptions.FromEnvironment(a2a);   // AGENT_NAME/ADVERTISE_URL/CAPABILITIES/REGISTRY
    var authn    = new A2aInboundAuthenticator(a2a, brokerKeyStore, registryClient);
    var listener = new A2aListener(orchestrator, a2a, authn, authFailures);

    var listenerTask = listener.RunAsync(cts.Token);   // blocking 수신 루프(Task)
    AgentRegistryLifecycle? lifecycle = null;
    if (regOpts.RegistryEnabled)
    {
        regOpts.ValidateOrThrow();   // ADVERTISE_URL 0.0.0.0/빈값 → 기동 거부(§D)
        lifecycle = new AgentRegistryLifecycle(
            registryClient, regOpts, brokerKeyStore, settings.Current.ModelId, authFailures);
        await lifecycle.StartAsync(cts.Token);   // register + 공개키 + heartbeat spawn(실패는 내부 graceful)
    }

    try { await listenerTask; }
    catch (OperationCanceledException) { /* Ctrl+C / 인증 실패 종료 — 정상 경로 */ }
    finally { if (lifecycle is not null) await lifecycle.StopAsync(); }   // best-effort deregister
    return authFatal ? HostExitCode.AuthFailure : HostExitCode.Ok;
}

var oneShot = cfg.Prompt ?? (Console.IsInputRedirected ? await Console.In.ReadToEndAsync() : null);
if (!string.IsNullOrWhiteSpace(oneShot))
    await host.RunOnceAsync(oneShot!, cts.Token);        // 파이프/인자 1회 처리
else
    await host.RunInteractiveAsync(cts.Token);           // 표준입력 프롬프트 루프

// 프로세스가 접히기 전에 반복 실행을 확실히 멈춘다 — 남은 턴이 종료 후에도 서버를 때리면 안 된다.
await loop.StopAndWaitAsync(LoopStopReason.HostShutdown);

// 인증 실패로 접힌 경우에만 비정상 종료 — 그래야 systemd 가 새 토큰으로 재시작을 건다.
return authFatal ? HostExitCode.AuthFailure : HostExitCode.Ok;

// ── 로컬 함수 ──

// Ctrl+C / SIGTERM 을 취소 토큰으로 변환한다. 첫 신호는 우아한 취소, 프로세스 강제 종료는 런타임에 위임.
static CancellationTokenSource ConsoleCancellation()
{
    var cts = new CancellationTokenSource();
    // 정상 종료 후에도 핸들러는 발화한다(ProcessExit 는 항상, CancelKeyPress 는 드물게) —
    // using 으로 dispose 된 CTS 에 Cancel() 하면 ObjectDisposedException 으로 종료 코드가 오염된다(WSL 스모크에서 실증).
    void SafeCancel()
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* 이미 정상 종료 경로 — 무시 */ }
    }
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;          // 즉시 종료 대신 협조적 취소
        SafeCancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeCancel();
    return cts;
}
