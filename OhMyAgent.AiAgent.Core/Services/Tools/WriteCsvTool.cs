using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>행 데이터를 CSV 파일로 기록한다(BCL 전용, 의존성 0). 보고서·내보내기 생성용.</summary>
public sealed class WriteCsvTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"rows":{"type":"array","items":{"type":"array","items":{"type":"string"}}},"headers":{"type":"array","items":{"type":"string"}},"delimiter":{"type":"string"}},"required":["path","rows"]}
        """);

    public string Name => "write_csv";
    public string Description => "Write rows (array of string arrays) to a CSV file in the workspace, with optional header row. Parent directories are created.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (!ToolPaths.TryGetRequiredPath(args, out var path, out var error))
            return error;

        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("rows", out var rowsEl)
            || rowsEl.ValueKind != JsonValueKind.Array)
            return ToolResult.Fail("rows(2차원 배열)가 필요합니다.");

        var delim = ToolSchemas.GetString(args, "delimiter") is { Length: > 0 } d ? d[0] : ',';

        var full = ctx.Workspace.ResolvePath(path);
        ToolPaths.EnsureParentDirectory(full);

        var sb = new StringBuilder();
        var rowCount = 0;

        if (args.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Array)
            AppendRow(sb, ToolSchemas.ToStringList(headersEl), delim);

        foreach (var rowEl in rowsEl.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            AppendRow(sb, ToolSchemas.ToStringList(rowEl), delim);
            rowCount++;
        }

        var written = await ToolPaths.WriteUtf8NoBomAsync(full, sb.ToString(), ct).ConfigureAwait(false);

        return ToolResult.Json(new { path, rows_written = rowCount, bytes_written = written });
    }

    private static void AppendRow(StringBuilder sb, List<string> fields, char delim)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(delim);
            sb.Append(Escape(fields[i], delim));
        }
        sb.Append("\r\n");
    }

    /// <summary>구분자·따옴표·개행 포함 시 따옴표로 감싸고 내부 따옴표는 "" 로 이스케이프(RFC 4180).</summary>
    private static string Escape(string value, char delim)
    {
        var needsQuote = value.IndexOf(delim) >= 0 || value.IndexOf('"') >= 0
                         || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0;
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
