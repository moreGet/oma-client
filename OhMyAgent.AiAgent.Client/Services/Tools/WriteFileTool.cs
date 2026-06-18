using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class WriteFileTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}
        """);

    public string Name => "write_file";
    public string Description => "Create or overwrite a UTF-8 text file in the workspace (parent directories are created).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        var content = ToolSchemas.GetString(args, "content");

        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("path 가 비어 있습니다.");
        content ??= "";

        var full = ctx.Workspace.ResolvePath(path);

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var bytes = new UTF8Encoding(false).GetBytes(content);
        await File.WriteAllBytesAsync(full, bytes, ct).ConfigureAwait(false);

        return ToolResult.Json(new { path, bytes_written = bytes.Length });
    }
}
