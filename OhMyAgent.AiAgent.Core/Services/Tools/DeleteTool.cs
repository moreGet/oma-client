using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class DeleteTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"recursive":{"type":"boolean"}},"required":["path"]}
        """);

    public string Name => "delete";
    public string Description => "Delete a file or directory within the workspace. Set recursive to delete a non-empty directory.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Destructive;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var recursive = ToolSchemas.GetBool(args, "recursive");

        if (!ToolPaths.TryResolvePath(args, ctx, out var path, out var full, out var error))
            return Task.FromResult(error);

        // R2: 작업 디렉토리 루트 자체 삭제 차단(예: path "." 또는 "sub/..").
        // 종전에는 주 루트(Roots[0])만 막아, 두 번째 워크스페이스 폴더가 통째로 지워졌다.
        if (SafeFileWalk.IsWorkspaceRootItself(ctx.Workspace, full))
            return Task.FromResult(ToolResult.Fail("작업 디렉토리 루트 자체는 삭제할 수 없습니다."));

        if (File.Exists(full))
        {
            File.Delete(full);
        }
        else if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive);
        }
        else
        {
            return Task.FromResult(ToolResult.Fail($"경로가 존재하지 않습니다: {path}"));
        }

        return Task.FromResult(ToolResult.Json(new { path, deleted = true }));
    }
}
