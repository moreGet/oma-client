using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>PDF에서 페이지별 텍스트를 추출한다(PdfPig, 순수 관리코드). 계약서·매뉴얼·보고서 읽기/요약용.</summary>
public sealed class ReadPdfTool : ITool
{
    private const int MaxChars = 200 * 1024; // ~200K 문자 캡

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"pages":{"type":"string"},"max_chars":{"type":"integer"}},"required":["path"]}
        """);

    public string Name => "read_pdf";
    public string Description => "Extract text from a PDF in the workspace. Optional 'pages' selects a 1-based range like \"1-5\" or \"3\". Returns text and page count.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Fail("path 가 비어 있습니다."));

        var full = ctx.Workspace.ResolvePath(path);
        if (!File.Exists(full))
            return Task.FromResult(ToolResult.Fail($"파일이 존재하지 않습니다: {path}"));

        var cap = ToolSchemas.GetInt(args, "max_chars") ?? MaxChars;

        using var doc = PdfDocument.Open(full);
        var pageCount = doc.NumberOfPages;
        var (from, to) = ParsePageRange(ToolSchemas.GetString(args, "pages"), pageCount);

        var sb = new StringBuilder();
        var truncated = false;
        for (var i = from; i <= to; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = doc.GetPage(i);
            var text = ContentOrderTextExtractor.GetText(page);
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("[p.").Append(i).Append("]\n").Append(text);

            if (sb.Length >= cap) { truncated = true; break; }
        }

        var outText = sb.Length > cap ? sb.ToString(0, cap) : sb.ToString();

        return Task.FromResult(ToolResult.Json(new
        {
            path,
            page_count = pageCount,
            pages = $"{from}-{to}",
            text = outText,
            truncated = truncated || sb.Length > cap,
        }));
    }

    /// <summary>"1-5" / "3" / null(전체)을 1-based 페이지 구간으로 보정(범위 클램프).</summary>
    private static (int From, int To) ParsePageRange(string? spec, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(spec)) return (1, pageCount);

        var parts = spec.Split('-', 2);
        if (!int.TryParse(parts[0].Trim(), out var from)) from = 1;
        var to = from;
        if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var t)) to = t;

        from = System.Math.Clamp(from, 1, pageCount);
        to = System.Math.Clamp(to, from, pageCount);
        return (from, to);
    }
}
