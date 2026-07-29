using System;
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
    private const int BufferChars = 8192;

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

        var (headers, rows, total) = await ParseCsvAsync(full, delim, hasHeader, maxRows, ct).ConfigureAwait(false);

        return ToolResult.Json(new
        {
            path,
            headers,
            rows,
            row_count = rows.Count,
            total_rows = total,
            truncated = total > rows.Count,
        });
    }

    /// <summary>
    /// RFC 4180 약식 파서 — 따옴표 안의 구분자/개행/이스케이프("")를 처리.
    /// 파일을 통째로 올리지 않고 한 버퍼씩 흘려보내며, 반환할 ≤maxRows 행만 보유하고
    /// 나머지는 세기만 하고 버린다 — 대용량 CSV 에서도 피크 메모리가 maxRows 에 비례한다.
    /// </summary>
    private static async Task<(string[]? Headers, List<string[]> Rows, int Total)> ParseCsvAsync(
        string full, char delim, bool hasHeader, int maxRows, CancellationToken ct)
    {
        string[]? headers = null;
        var rows = new List<string[]>();
        var total = 0;      // 헤더를 제외한 전체 행 수(보유하지 않은 행 포함).
        var rowIndex = 0;

        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        // 따옴표 안에서 방금 '"' 를 만났다 — 다음 문자가 '"' 면 이스케이프, 아니면 닫는 따옴표.
        // 원본의 1문자 lookahead 를 버퍼 경계에서도 안전하도록 상태로 바꾼 것.
        var pendingQuote = false;

        using var reader = new StreamReader(full, Encoding.UTF8);
        var buffer = new char[BufferChars];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (c == '"') { field.Append('"'); continue; }   // "" → 리터럴 따옴표
                    inQuotes = false;                                // 닫는 따옴표 — 이 문자는 아래에서 평문 처리
                }

                if (inQuotes)
                {
                    if (c == '"') pendingQuote = true;
                    else field.Append(c);
                    continue;
                }

                if (c == '"') inQuotes = true;
                else if (c == delim) { row.Add(field.ToString()); field.Clear(); }
                else if (c == '\r') { /* skip — \n 에서 행 종료 */ }
                else if (c == '\n')
                {
                    row.Add(field.ToString()); field.Clear();
                    EmitRow();
                }
                else field.Append(c);
            }
            ct.ThrowIfCancellationRequested();
        }

        // 마지막 행(개행 없이 끝나는 경우)
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            EmitRow();
        }

        return (headers, rows, total);

        // 완성된 행을 소비하고 버퍼(row)를 재사용한다 — 헤더 1행 또는 상한 이내의 행만 실제로 보유.
        void EmitRow()
        {
            if (hasHeader && rowIndex == 0)
            {
                headers = row.ToArray();
            }
            else
            {
                total++;
                if (rows.Count < maxRows) rows.Add(row.ToArray());
            }
            rowIndex++;
            row.Clear();
        }
    }
}
