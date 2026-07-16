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

        var skipped = new List<string>();   // 링크/접근 불가 — 조용히 빠뜨리지 않고 보고한다.

        if (File.Exists(src))
        {
            if (SafeFileWalk.IsLink(src))
                return Task.FromResult(ToolResult.Fail($"source 가 링크입니다(복사 대상에서 제외): {source}"));

            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
                Directory.CreateDirectory(dstDir);
            File.Copy(src, dst, overwrite);
        }
        else if (Directory.Exists(src))
        {
            CopyDirectory(src, dst, overwrite, skipped, ct);
        }
        else
        {
            return Task.FromResult(ToolResult.Fail($"source 가 존재하지 않습니다: {source}"));
        }

        return Task.FromResult(ToolResult.Json(new
        {
            source,
            destination,
            skipped_links = skipped.Count == 0 ? null : skipped
        }));
    }

    // 링크를 따라가지 않는다: 종전에는 하위 정션을 그대로 재귀해, 워크스페이스 밖 내용(예: .ssh)을
    // 워크스페이스 안으로 복사해 넣을 수 있었다. 링크를 따라가지 않으면 하위 트리를 벗어날 수 없다.
    private static void CopyDirectory(string src, string dst, bool overwrite, ICollection<string> skipped, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dst);

        foreach (var file in Directory.GetFiles(src))
        {
            ct.ThrowIfCancellationRequested();
            if (SafeFileWalk.IsLink(file)) { skipped.Add(file); continue; }
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite);
        }

        foreach (var dir in Directory.GetDirectories(src))
        {
            if (SafeFileWalk.IsLink(dir)) { skipped.Add(dir); continue; }
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)), overwrite, skipped, ct);
        }
    }
}
