using System.Collections.Generic;
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

    /// <summary>GET /api/v1/models — ModelInfo[]. 엔드포인트 없으면 빈 목록.</summary>
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/auth/login (Public). {username,password} → {token}. 성공 시 Token 보유한 LoginResult.</summary>
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);
}
