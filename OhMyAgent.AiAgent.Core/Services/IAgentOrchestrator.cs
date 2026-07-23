using System.Collections.Generic;
using System.Threading;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IAgentOrchestrator
{
    /// <summary>
    /// 하나의 목표를 완료(또는 Stop)까지 실행하며 AgentEvent 스트림을 VM 으로 방출.
    /// attachments는 사용자 메시지에 실린다.
    ///
    /// <paramref name="maxIterations"/> 로 반복 상한을 덮어쓸 수 있다(null 이면 설정값).
    /// 서브에이전트는 좁은 조사 임무만 맡으므로 메인 루프(기본 25)보다 낮게 잡아,
    /// 헤매는 하위 루프가 사용자 대기시간과 토큰을 잠식하지 않게 한다.
    ///
    /// <paramref name="session"/> 의 Messages 가 비어 있지 않으면 시스템 프롬프트를 시드하지 않는다 —
    /// 호출자가 미리 넣어 둔 프롬프트(예: 서브에이전트 전용)를 존중한다.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(
        string userGoal,
        AgentSession session,
        IReadOnlyList<Attachment>? attachments = null,
        CancellationToken ct = default,
        int? maxIterations = null);
}
