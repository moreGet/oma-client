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
        if (!ToolPaths.TryResolvePath(args, ctx, out var path, out var full, out var error))
            return error;

        var content = ToolSchemas.GetString(args, "content") ?? "";

        ToolPaths.EnsureParentDirectory(full);
        var written = await ToolPaths.WriteUtf8NoBomAsync(full, content, ct).ConfigureAwait(false);

        return ToolResult.Json(new { path, bytes_written = written });
    }
}
