using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class CopyTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"},"overwrite":{"type":"boolean"}},"required":["source","destination"]}
        """);

    public string Name => "copy";
    public string Description => "Copy a file or directory (recursively) within the workspace.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Destructive;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var source = ToolSchemas.GetString(args, "source");
        var destination = ToolSchemas.GetString(args, "destination");
        var overwrite = ToolSchemas.GetBool(args, "overwrite");

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            return Task.FromResult(ToolResult.Fail("source 와 destination 이 필요합니다."));

        var src = ctx.Workspace.ResolvePath(source);
        var dst = ctx.Workspace.ResolvePath(destination);

        if (File.Exists(src))
        {
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
                Directory.CreateDirectory(dstDir);
            File.Copy(src, dst, overwrite);
        }
        else if (Directory.Exists(src))
        {
            CopyDirectory(src, dst, overwrite);
        }
        else
        {
            return Task.FromResult(ToolResult.Fail($"source 가 존재하지 않습니다: {source}"));
        }

        return Task.FromResult(ToolResult.Json(new { source, destination }));
    }

    private static void CopyDirectory(string src, string dst, bool overwrite)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)), overwrite);
    }
}
