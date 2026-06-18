using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class ReadFileTool : ITool
{
    private const int MaxBytes = 200 * 1024; // ~200KB

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"}},"required":["path"]}
        """);

    public string Name => "read_file";
    public string Description => "Read a UTF-8 text file in the workspace. Optionally slice by 1-based inclusive line range.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("path 가 비어 있습니다.");

        var full = ctx.Workspace.ResolvePath(path);
        if (!File.Exists(full))
            return ToolResult.Fail($"파일이 존재하지 않습니다: {path}");

        var startLine = ToolSchemas.GetInt(args, "start_line");
        var endLine = ToolSchemas.GetInt(args, "end_line");

        string content;
        if (startLine.HasValue || endLine.HasValue)
        {
            var lines = await File.ReadAllLinesAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
            var from = Math.Max(1, startLine ?? 1);
            var to = Math.Min(lines.Length, endLine ?? lines.Length);
            if (from > lines.Length)
                return ToolResult.Ok("");
            content = string.Join('\n', lines.Skip(from - 1).Take(Math.Max(0, to - from + 1)));
        }
        else
        {
            content = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxBytes)
        {
            var truncated = content.Length > MaxBytes ? content[..MaxBytes] : content;
            return ToolResult.Ok(truncated + $"\n\n[... 출력이 {MaxBytes} 바이트로 잘렸습니다 ...]");
        }

        return ToolResult.Ok(content);
    }
}
