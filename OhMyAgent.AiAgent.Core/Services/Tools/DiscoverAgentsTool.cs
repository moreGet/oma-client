using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// discover_agents (Risk=ReadOnly) — 레지스트리에서 capability/status/query 로 동료 에이전트를 찾는다.
/// 정보 노출뿐이라 Read. 자기 자신은 exclude_self(=keyStore.AgentId)로 제외(미등록이면 null → 제외 없음).
/// raw url 필드는 스키마에 두지 않는다(SSRF 방지 일관성).
/// </summary>
public sealed class DiscoverAgentsTool(IAgentRegistryClient registry, IBrokerKeyStore keyStore) : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"capability":{"type":"string"},"query":{"type":"string"},"status":{"type":"string","enum":["online"]}},"required":[]}
        """);

    public string Name => "discover_agents";

    public string Description =>
        "Find other agents registered in the shared registry to delegate work to. " +
        "Args: capability (filter by capability tag), query (free-text), status ('online' for live agents only). " +
        "Returns a list of candidates {agent_id, name, capabilities, status}. Use ask_agent to actually delegate to one.";

    public JsonElement ParametersSchema => Schema;

    public ToolRisk Risk => ToolRisk.ReadOnly;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var capability = ToolSchemas.GetString(args, "capability");
        var query      = ToolSchemas.GetString(args, "query");
        var status     = ToolSchemas.GetString(args, "status");

        var descriptors = await registry.DiscoverAsync(
            new DiscoverQuery(Capability: capability, Status: status, Query: query, ExcludeSelf: keyStore.AgentId),
            ct).ConfigureAwait(false);

        var agents = descriptors.Select(d => new
        {
            agent_id     = d.AgentId,
            name         = d.Name,
            capabilities = d.Capabilities,
            status       = d.Status,
        }).ToList();

        return ToolResult.Json(new { agents });
    }
}
