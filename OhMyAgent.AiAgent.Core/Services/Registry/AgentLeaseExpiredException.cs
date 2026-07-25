using System;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// heartbeat 가 404 를 받았을 때(소유자 아님/리스 만료로 서버가 정리) 던지는 typed 신호.
/// lifecycle 루프가 이것을 잡아 재-register 로 자가 치유한다(§공유 계약 2).
///
/// AgentException 을 상속하지 않는 이유: AgentException 은 sealed 라 상속 불가. Exception 을 직접
/// 상속하고, lifecycle 루프가 일반 catch 보다 앞에서 명시적으로 잡는다.
/// </summary>
public sealed class AgentLeaseExpiredException(string agentId)
    : Exception($"에이전트 리스가 만료되었거나 소유자가 아닙니다(재등록 필요): {agentId}")
{
    public string AgentId { get; } = agentId;
}
