using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class CreateDirectoryTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}
        """);

    public string Name => "create_directory";
    public string Description => "Create a directory (and any missing parents) inside the workspace.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Fail("path 가 비어 있습니다."));

        var full = ctx.Workspace.ResolvePath(path);
        Directory.CreateDirectory(full);
        return Task.FromResult(ToolResult.Json(new { path }));
    }
}
