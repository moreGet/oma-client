using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 에이전트 레지스트리(전화번호부) REST 클라이언트(§공유 계약). 기존 AgentApiClient 처럼
/// ISettingsService 의 ServerBaseUrl + AuthToken(JWT)으로 Go 서버 컨트롤플레인을 호출한다.
/// 실패는 도메인 예외(AgentException / AgentLeaseExpiredException) 또는 graceful 빈값/ null 로 변환한다.
/// </summary>
public interface IAgentRegistryClient
{
    Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default);

    /// <summary>404(소유자 아님/리스 만료 정리 포함) → <see cref="AgentLeaseExpiredException"/>.</summary>
    Task<HeartbeatResponse> HeartbeatAsync(string agentId, CancellationToken ct = default);

    /// <summary>우아한 해제. 204/200/404 모두 성공 간주(멱등), 오프라인 → best-effort no-op.</summary>
    Task DeregisterAsync(string agentId, CancellationToken ct = default);

    /// <summary>발견. 비2xx/오프라인 → 빈 목록(도구가 "후보 없음"으로 처리).</summary>
    Task<IReadOnlyList<AgentDescriptor>> DiscoverAsync(DiscoverQuery query, CancellationToken ct = default);

    /// <summary>단건 조회. 404/비2xx/오프라인 → null.</summary>
    Task<AgentDescriptor?> GetAsync(string agentId, CancellationToken ct = default);

    /// <summary>대상별 단명 A2A 토큰 발급(브로커). 404(대상 소멸) → <see cref="AgentException"/>.</summary>
    Task<A2aToken> MintA2aTokenAsync(string targetAgentId, CancellationToken ct = default);

    /// <summary>수신측 서명 검증용 공개키 취득.</summary>
    Task<A2aPublicKey> GetA2aPublicKeyAsync(CancellationToken ct = default);
}
