using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// ask_agent (Risk=Execute) — 발견한 동료 에이전트에 작업을 위임하고 결과를 종합한다.
///
/// 보안 핵심:
///   - args 에 raw url/endpoint 필드가 없다 → 엔드포인트는 반드시 레지스트리 descriptor 에서만 해석(SSRF 봉쇄).
///   - 대상별 단명 토큰(120s)을 호출 직전 발급(캐시 안 함).
///   - hop = ctx.InboundHop + 1 로 전파(수신 홉 이어받아 순환 방어).
/// </summary>
public sealed class AskAgentTool(IAgentRegistryClient registry, A2aChatClient chat, IBrokerKeyStore keyStore) : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"agent_id":{"type":"string"},"capability":{"type":"string"},"prompt":{"type":"string"}},"required":["prompt"]}
        """);

    public string Name => "ask_agent";

    public string Description =>
        "Delegate a task to another registered agent and get its answer. " +
        "Provide 'prompt' (required) plus either 'agent_id' (a specific agent from discover_agents) or " +
        "'capability' (auto-pick the first online agent with that capability). " +
        "The endpoint is resolved from the registry — you cannot supply a raw URL.";

    public JsonElement ParametersSchema => Schema;

    public ToolRisk Risk => ToolRisk.Execute;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var prompt = ToolSchemas.GetString(args, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("prompt 가 비어 있습니다.");

        var agentId    = ToolSchemas.GetString(args, "agent_id");
        var capability = ToolSchemas.GetString(args, "capability");

        // 1) 대상 해석 — 레지스트리에서만(모델 입력 URL 불가).
        AgentDescriptor? target;
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            target = await registry.GetAsync(agentId, ct).ConfigureAwait(false);
            if (target is null)
                return ToolResult.Fail($"대상 에이전트를 찾을 수 없습니다: {agentId}");
        }
        else if (!string.IsNullOrWhiteSpace(capability))
        {
            var candidates = await registry.DiscoverAsync(
                new DiscoverQuery(Capability: capability, Status: "online", ExcludeSelf: keyStore.AgentId),
                ct).ConfigureAwait(false);
            if (candidates.Count == 0)
                return ToolResult.Fail($"'{capability}' capability 를 가진 온라인 에이전트가 없습니다.");

            // 모호해도 결정적으로 첫 후보 선택. 선택 결과를 명시(어느 에이전트를 골랐는지).
            target = candidates[0];
        }
        else
        {
            return ToolResult.Fail("agent_id 또는 capability 중 하나를 지정해야 합니다.");
        }

        // 2) 엔드포인트 — 해석된 descriptor 의 endpoint_url 만 사용(SSRF 봉쇄).
        var endpoint = target.EndpointUrl;
        if (string.IsNullOrWhiteSpace(endpoint))
            return ToolResult.Fail($"대상 에이전트({target.Name})의 엔드포인트가 비어 있습니다.");

        // 3) 토큰 발급(호출 직전, 단명 → 캐시 안 함).
        A2aToken token;
        try
        {
            token = await registry.MintA2aTokenAsync(target.AgentId, ct).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        // 4) 홉 전파(수신 홉 +1) + A2A 호출.
        var outHop = ctx.InboundHop + 1;
        var result = await chat.AskAsync(endpoint, token.Token, prompt!, outHop, ct).ConfigureAwait(false);

        if (result.IsError)
            return ToolResult.Fail($"[{target.Name}] 위임 실패: {result.ErrorMessage}");

        var header = $"[{target.Name} ({target.AgentId})]";
        return ToolResult.Ok(string.IsNullOrWhiteSpace(result.Text)
            ? $"{header} (빈 응답)"
            : $"{header}\n{result.Text}");
    }
}
