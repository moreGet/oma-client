namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// broker 모드 공유 슬롯 — 자기 agent_id(등록 성공 시 확정, broker aud 검증 기준)와 캐시 공개키를 보관.
/// lifecycle(Host)이 write, 협업 도구(Core)와 A2aInboundAuthenticator(Host)가 read 하므로 계약은 Core.
/// 구현은 thread-safe 여야 한다(리스너 다중 요청 ↔ heartbeat 루프 동시 접근).
/// </summary>
public interface IBrokerKeyStore
{
    /// <summary>register 성공 시 set 되는 자기 agent_id. 미등록이면 null(broker 수신 401 사유).</summary>
    string? AgentId { get; }

    /// <summary>캐시된 브로커 공개키(kid+PEM). 없으면 false.</summary>
    bool TryGetKey(out string kid, out string pem);

    void SetAgentId(string id);

    void SetKey(string kid, string pem);
}
