using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>.xlsx 워크북에서 셀 값을 행 단위로 읽는다(ClosedXML). 데이터 추출·집계·요약용.</summary>
public sealed class ReadExcelTool : ITool
{
    private const int DefaultMaxRows = 1000;

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"sheet":{"type":"string"},"range":{"type":"string"},"max_rows":{"type":"integer"}},"required":["path"]}
        """);

    public string Name => "read_excel";
    public string Description => "Read cell values from an .xlsx workbook as rows. 'sheet' is a name or 1-based index (default first); optional A1-style 'range' (e.g. A1:D50).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (!ToolPaths.TryResolveExistingFile(args, ctx, out _, out var full, out var error))
            return Task.FromResult(error);

        var sheetArg = ToolSchemas.GetString(args, "sheet");
        var rangeArg = ToolSchemas.GetString(args, "range");
        var maxRows = ToolSchemas.GetInt(args, "max_rows") ?? DefaultMaxRows;

        using var wb = new XLWorkbook(full);

        IXLWorksheet ws;
        if (string.IsNullOrWhiteSpace(sheetArg))
        {
            ws = wb.Worksheet(1);
        }
        else if (int.TryParse(sheetArg, out var idx))
        {
            if (idx < 1 || idx > wb.Worksheets.Count)
                return Task.FromResult(ToolResult.Fail($"시트 인덱스 범위를 벗어남: {idx} (총 {wb.Worksheets.Count}개)"));
            ws = wb.Worksheet(idx);
        }
        else if (!wb.TryGetWorksheet(sheetArg, out ws!))
        {
            return Task.FromResult(ToolResult.Fail($"시트를 찾을 수 없음: {sheetArg}"));
        }

        var area = string.IsNullOrWhiteSpace(rangeArg) ? ws.RangeUsed() : ws.Range(rangeArg);
        if (area is null)
            return Task.FromResult(ToolResult.Json(new { sheet = ws.Name, rows = new string[0][], row_count = 0, truncated = false }));

        var rows = new List<List<string>>();
        var allRows = area.Rows();
        var total = 0;
        foreach (var r in allRows)
        {
            total++;
            if (rows.Count >= maxRows) continue;
            ct.ThrowIfCancellationRequested();
            var cells = new List<string>();
            foreach (var cell in r.Cells())
                cells.Add(cell.GetString());
            rows.Add(cells);
        }

        return Task.FromResult(ToolResult.Json(new
        {
            sheet = ws.Name,
            rows,
            row_count = rows.Count,
            total_rows = total,
            truncated = total > rows.Count,
        }));
    }
}
