using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class ListDirectoryTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"}}}
        """);

    public string Name => "list_directory";
    public string Description => "List entries (files and directories) in a workspace directory. Defaults to the workspace root.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        var full = ctx.Workspace.ResolvePath(path ?? "");

        if (!Directory.Exists(full))
            return Task.FromResult(ToolResult.Fail($"디렉토리가 존재하지 않습니다: {(string.IsNullOrEmpty(path) ? "." : path)}"));

        var entries = Directory.EnumerateFileSystemEntries(full)
            .Select(e =>
            {
                var isDir = Directory.Exists(e);
                long size = 0;
                if (!isDir)
                {
                    try { size = new FileInfo(e).Length; } catch { /* ignore */ }
                }
                return new
                {
                    name = Path.GetFileName(e),
                    type = isDir ? "dir" : "file",
                    size
                };
            })
            .OrderByDescending(e => e.type == "dir")
            .ThenBy(e => e.name)
            .ToList();

        return Task.FromResult(ToolResult.Json(new { entries }));
    }
}
