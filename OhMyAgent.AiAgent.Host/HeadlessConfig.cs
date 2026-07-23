using System;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// 환경 변수로 헤드리스 호스트를 설정한다(서버 URL/토큰/모델/워크스페이스/승인 모드/프롬프트).
/// Linux 헤드리스는 토큰 비영속 모델 — 매 기동 시 env 로 주입(스펙 §1-F).
/// </summary>
public sealed record HeadlessConfig(
    string ServerBaseUrl,
    string AuthToken,
    string ModelId,
    int? MaxIterations,
    string? WorkspaceRoot,
    string ApprovalMode,
    string? Prompt)
{
    public static HeadlessConfig FromEnvironment()
    {
        string Get(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

        var maxIterRaw = Get("OHMYAGENT_MAX_ITERATIONS");
        int? maxIter = int.TryParse(maxIterRaw, out var mi) && mi > 0 ? mi : null;

        var workspace = Get("OHMYAGENT_WORKSPACE");
        var prompt = Environment.GetEnvironmentVariable("OHMYAGENT_PROMPT");

        var approval = Get("OHMYAGENT_HEADLESS_APPROVAL");
        if (string.IsNullOrEmpty(approval)) approval = "deny";

        return new HeadlessConfig(
            ServerBaseUrl: Get("OHMYAGENT_SERVER_URL"),
            AuthToken:     Get("OHMYAGENT_AUTH_TOKEN"),
            ModelId:       Get("OHMYAGENT_MODEL"),
            MaxIterations: maxIter,
            WorkspaceRoot: string.IsNullOrWhiteSpace(workspace) ? null : workspace,
            ApprovalMode:  approval,
            Prompt:        string.IsNullOrWhiteSpace(prompt) ? null : prompt);
    }
}
