using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>워크스페이스 내 CSV 파일을 읽어 헤더/행으로 구조화한다(BCL 전용, 의존성 0).</summary>
public sealed class ReadCsvTool : ITool
{
    private const int DefaultMaxRows = 1000;

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"delimiter":{"type":"string"},"has_header":{"type":"boolean"},"max_rows":{"type":"integer"}},"required":["path"]}
        """);

    public string Name => "read_csv";
    public string Description => "Read a CSV/TSV file in the workspace into headers and rows. Use for tabular data extraction, filtering, and summaries.";
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

        var delim = ToolSchemas.GetString(args, "delimiter") is { Length: > 0 } d ? d[0] : ',';
        var hasHeader = ToolSchemas.GetBool(args, "has_header", true);
        var maxRows = ToolSchemas.GetInt(args, "max_rows") ?? DefaultMaxRows;

        var text = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
        var records = ParseCsv(text, delim);

        string[]? headers = null;
        var start = 0;
        if (hasHeader && records.Count > 0)
        {
            headers = records[0].ToArray();
            start = 1;
        }

        var rows = new List<string[]>();
        var total = records.Count - start;
        for (var i = start; i < records.Count && rows.Count < maxRows; i++)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(records[i].ToArray());
        }

        return ToolResult.Json(new
        {
            path,
            headers,
            rows,
            row_count = rows.Count,
            total_rows = total < 0 ? 0 : total,
            truncated = total > rows.Count,
        });
    }

    /// <summary>RFC 4180 약식 파서 — 따옴표 안의 구분자/개행/이스케이프("")를 처리.</summary>
    private static List<List<string>> ParseCsv(string text, char delim)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delim) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* skip — \n 에서 행 종료 */ }
            else if (c == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
            }
            else field.Append(c);
        }

        // 마지막 행(개행 없이 끝나는 경우)
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
