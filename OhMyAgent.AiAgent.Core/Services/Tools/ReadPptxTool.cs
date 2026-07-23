using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// PowerPoint .pptx 에서 슬라이드별 텍스트를 추출한다. .pptx 는 zip+XML 이라 BCL 만으로 처리(의존성 0).
/// </summary>
public sealed partial class ReadPptxTool : ITool
{
    private const int MaxChars = 200 * 1024;
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex SlideRe();

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"max_chars":{"type":"integer"}},"required":["path"]}
        """);

    public string Name => "read_pptx";
    public string Description => "Extract per-slide text from a PowerPoint .pptx file in the workspace. Use for reading and summarizing presentations.";
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

        try
        {
            using var zip = ZipFile.OpenRead(full);
            var slideEntries = zip.Entries
                .Select(e => (e, m: SlideRe().Match(e.FullName)))
                .Where(x => x.m.Success)
                .OrderBy(x => int.Parse(x.m.Groups[1].Value))
                .Select(x => x.e)
                .ToList();

            if (slideEntries.Count == 0)
                return Task.FromResult(ToolResult.Fail("ppt/slides/slideN.xml 을 찾을 수 없습니다(.pptx 형식이 아닐 수 있음)."));

            var slides = new List<object>();
            var total = 0;
            var truncated = false;
            var index = 0;
            foreach (var entry in slideEntries)
            {
                ct.ThrowIfCancellationRequested();
                index++;
                using var s = entry.Open();
                var doc = XDocument.Load(s);
                var sb = new StringBuilder();
                foreach (var p in doc.Descendants(A + "p"))
                {
                    foreach (var t in p.Descendants(A + "t")) sb.Append(t.Value);
                    sb.Append('\n');
                }
                var text = sb.ToString().TrimEnd('\n');
                total += text.Length;
                if (total > cap) { truncated = true; break; }
                slides.Add(new { slide = index, text });
            }

            return Task.FromResult(ToolResult.Json(new { path, slide_count = slideEntries.Count, slides, truncated }));
        }
        catch (InvalidDataException)
        {
            return Task.FromResult(ToolResult.Fail($"유효한 .pptx(zip) 가 아닙니다: {path}"));
        }
    }
}
