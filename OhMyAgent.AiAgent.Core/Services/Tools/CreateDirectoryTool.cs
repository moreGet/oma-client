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
        if (!ToolPaths.TryResolvePath(args, ctx, out var path, out var full, out var error))
            return Task.FromResult(error);

        Directory.CreateDirectory(full);
        return Task.FromResult(ToolResult.Json(new { path }));
    }
}
