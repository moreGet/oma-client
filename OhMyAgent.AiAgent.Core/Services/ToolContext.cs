using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 모든 도구에 전달되는 주변 실행 컨텍스트 (도구 내부 DI 없음).
/// 서비스 참조(IWorkspaceContext)를 들고 있어 Services 레이어에 위치.
/// </summary>
public sealed record ToolContext(
    IWorkspaceContext Workspace,
    PermissionMode PermissionMode);
