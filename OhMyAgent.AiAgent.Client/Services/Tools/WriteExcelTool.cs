using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>행 데이터를 .xlsx 워크북으로 생성/추가한다(ClosedXML). 보고서·집계표 생성용.</summary>
public sealed class WriteExcelTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"sheet":{"type":"string"},"rows":{"type":"array","items":{"type":"array","items":{"type":"string"}}},"headers":{"type":"array","items":{"type":"string"}},"mode":{"type":"string","enum":["create","append"]}},"required":["path","rows"]}
        """);

    public string Name => "write_excel";
    public string Description => "Write rows to an .xlsx worksheet. mode 'create' (default) makes a new workbook; 'append' adds rows under an existing sheet (creating the file if missing).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Fail("path 가 비어 있습니다."));

        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("rows", out var rowsEl)
            || rowsEl.ValueKind != JsonValueKind.Array)
            return Task.FromResult(ToolResult.Fail("rows(2차원 배열)가 필요합니다."));

        var sheetName = ToolSchemas.GetString(args, "sheet") is { Length: > 0 } s ? s : "Sheet1";
        var append = string.Equals(ToolSchemas.GetString(args, "mode"), "append", System.StringComparison.OrdinalIgnoreCase);

        var full = ctx.Workspace.ResolvePath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        XLWorkbook wb;
        IXLWorksheet ws;
        var rowCursor = 1;

        if (append && File.Exists(full))
        {
            wb = new XLWorkbook(full);
            if (!wb.TryGetWorksheet(sheetName, out ws!))
                ws = wb.Worksheets.Add(sheetName);
            else
                rowCursor = (ws.LastRowUsed()?.RowNumber() ?? 0) + 1;
        }
        else
        {
            wb = new XLWorkbook();
            ws = wb.Worksheets.Add(sheetName);
        }

        using (wb)
        {
            // 헤더는 새로 만들 때(또는 빈 시트에 append) 1행에만 기록.
            if (rowCursor == 1
                && args.TryGetProperty("headers", out var headersEl)
                && headersEl.ValueKind == JsonValueKind.Array)
            {
                WriteRow(ws, rowCursor++, ToolSchemas.ToStringList(headersEl));
            }

            var written = 0;
            foreach (var rowEl in rowsEl.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                WriteRow(ws, rowCursor++, ToolSchemas.ToStringList(rowEl));
                written++;
            }

            wb.SaveAs(full);
            return Task.FromResult(ToolResult.Json(new { path, sheet = ws.Name, rows_written = written }));
        }
    }

    private static void WriteRow(IXLWorksheet ws, int rowNumber, List<string> fields)
    {
        for (var c = 0; c < fields.Count; c++)
            ws.Cell(rowNumber, c + 1).Value = fields[c];
    }
}
