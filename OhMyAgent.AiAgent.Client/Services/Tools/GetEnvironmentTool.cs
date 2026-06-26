using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class GetEnvironmentTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"name":{"type":"string"}}}
        """);

    public string Name => "get_environment";
    public string Description => "Read environment variables. Provide 'name' for a single variable, or omit it to return the full environment map. Values are returned as-is (local agent).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var name = ToolSchemas.GetString(args, "name");

        if (!string.IsNullOrEmpty(name))
        {
            var value = System.Environment.GetEnvironmentVariable(name);
            if (value is null)
                return Task.FromResult(ToolResult.Fail($"환경변수가 존재하지 않습니다: {name}"));
            return Task.FromResult(ToolResult.Json(new { name, value }));
        }

        var map = new Dictionary<string, string?>();
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (!string.IsNullOrEmpty(key))
                map[key] = entry.Value?.ToString();
        }

        var ordered = map
            .OrderBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, System.StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(ToolResult.Json(new { count = ordered.Count, variables = ordered }));
    }
}
