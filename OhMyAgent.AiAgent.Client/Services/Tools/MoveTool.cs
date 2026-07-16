using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class MoveTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"},"overwrite":{"type":"boolean"}},"required":["source","destination"]}
        """);

    public string Name => "move";
    public string Description => "Move or rename a file or directory within the workspace.";
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

        // R2: 작업 디렉토리 루트 자체를 이동하거나 루트를 덮어쓰는 것 차단.
        // 종전에는 주 루트(Roots[0])만 막아, 두 번째 워크스페이스 폴더는 무방비였다.
        if (SafeFileWalk.IsWorkspaceRootItself(ctx.Workspace, src))
            return Task.FromResult(ToolResult.Fail("작업 디렉토리 루트 자체는 이동할 수 없습니다."));
        if (SafeFileWalk.IsWorkspaceRootItself(ctx.Workspace, dst))
            return Task.FromResult(ToolResult.Fail("작업 디렉토리 루트를 덮어쓸 수 없습니다."));

        if (File.Exists(src))
        {
            if (overwrite && File.Exists(dst))
                File.Delete(dst);
            File.Move(src, dst, overwrite);
        }
        else if (Directory.Exists(src))
        {
            if (Directory.Exists(dst))
            {
                if (!overwrite)
                    return Task.FromResult(ToolResult.Fail($"대상이 이미 존재합니다: {destination}"));
                Directory.Delete(dst, recursive: true);
            }
            Directory.Move(src, dst);
        }
        else
        {
            return Task.FromResult(ToolResult.Fail($"source 가 존재하지 않습니다: {source}"));
        }

        return Task.FromResult(ToolResult.Json(new { source, destination }));
    }
}
