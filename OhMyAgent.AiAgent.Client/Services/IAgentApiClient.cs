using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IAgentApiClient
{
    /// <summary>POST /api/v1/agent/chat (stream:true) — SSE 를 파싱해 AgentStreamEvent 방출.</summary>
    IAsyncEnumerable<AgentStreamEvent> SendAsync(AgentRequest request, CancellationToken ct = default);

    /// <summary>GET /api/v1/health — 200 + {status:"ok"}.</summary>
    Task<bool> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// 연결 + 인증을 함께 점검한다. /health(Public) 도달 여부와 토큰 유효성(보호된 엔드포인트 401 여부)을
    /// 묶어 <see cref="ServerReadiness"/> 로 반환 — 화면이 "연결 실패"와 "로그인 필요"를 구분하도록.
    /// </summary>
    Task<ServerReadiness> CheckReadinessAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/models — ModelInfo[]. 엔드포인트 없으면 빈 목록.</summary>
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/auth/login (Public). {username,password} → {token}. 성공 시 Token 보유한 LoginResult.</summary>
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);

    /// <summary>GET /api/v1/users/me (Bearer). 200→UserProfile, 401/404/오프라인→null(graceful).</summary>
    Task<UserProfile?> GetProfileAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/client/version (Bearer). 200→ClientVersionInfo, 그 외/404/오프라인→null(graceful, 예외 없음).</summary>
    Task<ClientVersionInfo?> GetClientVersionAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/me/quota (Bearer). 200→QuotaResponse, 401/500/오프라인/파싱실패→null(graceful, 예외 없음).</summary>
    Task<QuotaResponse?> GetQuotaAsync(CancellationToken ct = default);

    // ── 서버 도구 정책 게이트. 미구현 서버(404/501)/오프라인은 graceful null. ──

    /// <summary>GET /api/v1/tools/policy (Bearer). 200→ToolPolicy, 404/오류/오프라인→null(graceful).</summary>
    Task<ToolPolicy?> GetToolPolicyAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/tools/authorize (Bearer). {tool,arguments} → ToolAuthorization. 200→객체, 오류/오프라인→null.</summary>
    Task<ToolAuthorization?> AuthorizeToolAsync(string tool, JsonElement arguments, CancellationToken ct = default);

    // ── 프로젝트 서버 동기화(선택). 미구현 서버(404/501)는 graceful 실패로 다룬다. ──

    /// <summary>GET /api/v1/projects — 원격 프로젝트 목록. 미지원/오프라인이면 빈 목록.</summary>
    Task<IReadOnlyList<RemoteProject>> ListRemoteProjectsAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/projects — 원격 프로젝트 upsert. 실패 시 AgentException.</summary>
    Task<RemoteProject> UpsertRemoteProjectAsync(RemoteProjectUpsert body, CancellationToken ct = default);

    /// <summary>POST /api/v1/projects/{remoteProjectId}/conversations — 대화 push. 실패 시 AgentException.</summary>
    Task UpsertRemoteConversationAsync(string remoteProjectId, RemoteConversation body, CancellationToken ct = default);

    /// <summary>DELETE /api/v1/projects/{remoteProjectId} — 원격 프로젝트 삭제. 미지원/오프라인/404는 graceful no-op.</summary>
    Task DeleteRemoteProjectAsync(string remoteProjectId, CancellationToken ct = default);

    /// <summary>DELETE /api/v1/projects/{remoteProjectId}/conversations/{remoteConversationId} — 원격 대화 삭제. 미지원/오프라인/404는 graceful no-op.</summary>
    Task DeleteRemoteConversationAsync(string remoteProjectId, string remoteConversationId, CancellationToken ct = default);
}
