using System.Collections.Generic;
using System.Threading;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IAgentOrchestrator
{
    /// <summary>하나의 목표를 완료(또는 Stop)까지 실행하며 AgentEvent 스트림을 VM 으로 방출. attachments는 사용자 메시지에 실린다.</summary>
    IAsyncEnumerable<AgentEvent> RunAsync(
        string userGoal,
        AgentSession session,
        IReadOnlyList<Attachment>? attachments = null,
        CancellationToken ct = default);
}
