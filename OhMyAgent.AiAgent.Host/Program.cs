using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using OhMyAgent.AiAgent.Host;

AppLog.Initialize();
AppLog.Info("Host", $"헤드리스 시작 — v{AppVersion.Full}");

var cfg = HeadlessConfig.FromEnvironment();   // OHMYAGENT_SERVER_URL / _AUTH_TOKEN / _MODEL / _WORKSPACE / _HEADLESS_APPROVAL

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

// 2) HTTP — App 과 동일하게 BaseAddress 미설정(요청마다 절대 URI). 무한 타임아웃.
var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var toolHttp   = HttpFetchTool.CreateDefaultClient();   // internal → Core IVT(Host)

// 3) 워크스페이스 샌드박스
var workspace = new WorkspaceContext(settings);

// 4) 스크립트 실행기(run_command 엔진) — OS 라우팅은 ScriptExecutor 내부.
var scriptExec = new ScriptExecutor();

// 5) 공유 서비스
var todoService = new TodoService();

// 6) headless-safe 도구 (Clipboard 2종 + Screenshot 제외)
var tools = HeadlessAgentHost.BuildHeadlessTools(scriptExec, toolHttp, todoService);

// 7) 권한 게이트 — 핸들러는 정책에 따라 등록. 미등록이면 gated=Deny(안전 기본).
var permissions = new PermissionService(settings);
HeadlessPermissionPolicy.Apply(permissions, cfg.ApprovalMode);   // "deny"(기본) | "auto"

// 8) API/정책/컴팩터
var api        = new AgentApiClient(httpClient, settings);
var toolPolicy = new ToolPolicyService(api);
var compactor  = new ContextCompactor(api, settings);

// 9) 서브에이전트(task) — 재귀 방지 위해 task 도구 없는 레지스트리로 조립.
var subagentTools   = tools.Where(t => TaskTool.AllowedToolNames.Contains(t.Name)).ToArray();
var subOrchestrator = new AgentOrchestrator(
    api, new ToolRegistry(subagentTools), permissions, workspace, settings, toolPolicy, compactor,
    suppressThinking: true);

// 10) 메인 레지스트리 = 기본 도구 + task
var registry = new ToolRegistry([.. tools, new TaskTool(subOrchestrator, workspace)]);

// 11) 오케스트레이터
var orchestrator = new AgentOrchestrator(api, registry, permissions, workspace, settings, toolPolicy, compactor);

// 12) 실행 코어 + 모드 분기
var host = new HeadlessAgentHost(orchestrator);
using var cts = ConsoleCancellation();   // Ctrl+C / SIGTERM → 취소 토큰

var oneShot = cfg.Prompt ?? (Console.IsInputRedirected ? await Console.In.ReadToEndAsync() : null);
if (!string.IsNullOrWhiteSpace(oneShot))
    await host.RunOnceAsync(oneShot!, cts.Token);        // 파이프/인자 1회 처리
else
    await host.RunInteractiveAsync(cts.Token);           // 표준입력 프롬프트 루프

return;

// ── 로컬 함수 ──

// Ctrl+C / SIGTERM 을 취소 토큰으로 변환한다. 첫 신호는 우아한 취소, 프로세스 강제 종료는 런타임에 위임.
static CancellationTokenSource ConsoleCancellation()
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;          // 즉시 종료 대신 협조적 취소
        cts.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
    return cts;
}
