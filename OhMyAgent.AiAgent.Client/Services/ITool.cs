using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>샌드박스 도구 계약. 각 도구는 stateless singleton.</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema object (도구의 parameters).</summary>
    JsonElement ParametersSchema { get; }

    ToolRisk Risk { get; }

    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default);
}
