using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class GlobTool : ITool
{
    private const int Cap = 1000;

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"}},"required":["pattern"]}
        """);

    public string Name => "glob";
    public string Description => "Find files matching a glob pattern (e.g. **/*.cs) under the workspace or a subdirectory.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var pattern = ToolSchemas.GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return Task.FromResult(ToolResult.Fail("pattern 이 비어 있습니다."));

        var path = ToolSchemas.GetString(args, "path");
        var baseDir = ctx.Workspace.ResolvePath(path ?? "");

        if (!Directory.Exists(baseDir))
            return Task.FromResult(ToolResult.Fail($"디렉토리가 존재하지 않습니다: {(string.IsNullOrEmpty(path) ? "." : path)}"));

        var files = GlobMatcher.EnumerateFiles(baseDir, pattern, Cap).ToList();

        // 0건은 오류가 아니지만 막다른 길이다 — 닮은 이름 후보와 대안(내용 검색/되묻기)을 힌트로 싣는다.
        if (files.Count == 0)
        {
            return Task.FromResult(ToolResult.Json(new
            {
                files,
                truncated = false,
                hint = NotFoundHelp.ForGlob(ctx.Workspace, pattern, ct)
            }));
        }

        return Task.FromResult(ToolResult.Json(new { files, truncated = files.Count >= Cap }));
    }
}
