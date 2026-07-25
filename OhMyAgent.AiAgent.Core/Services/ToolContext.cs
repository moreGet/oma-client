using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 모든 도구에 전달되는 주변 실행 컨텍스트 (도구 내부 DI 없음).
/// 서비스 참조(IWorkspaceContext)를 들고 있어 Services 레이어에 위치.
/// </summary>
public sealed record ToolContext(
    IWorkspaceContext Workspace,
    PermissionMode PermissionMode,
    // A2A 수신 홉. 리스너 경로에서만 >0(요청별 AgentSession.InboundHop 에서 주입). ask_agent 가 +1 전파해
    // 순환/무한 위임을 유한 깊이에서 봉쇄한다. 비-리스너 경로는 0(최초 홉=1).
    int InboundHop = 0);
