using System;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// Word .docx 에서 본문 텍스트를 추출한다. .docx 는 zip+XML 이라 BCL(System.IO.Packaging)만으로 처리(의존성 0).
/// </summary>
public sealed class ReadDocumentTool : ITool
{
    private const int MaxChars = 200 * 1024;

    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly Uri DocumentPart =
        new("/word/document.xml", UriKind.Relative);

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"max_chars":{"type":"integer"}},"required":["path"]}
        """);

    public string Name => "read_document";
    public string Description => "Extract body text from a Word .docx file in the workspace (paragraphs preserved). Use for reading and summarizing documents.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (!ToolPaths.TryResolveExistingFile(args, ctx, out var path, out var full, out var error))
            return Task.FromResult(error);

        var cap = ToolSchemas.GetInt(args, "max_chars") ?? MaxChars;

        XDocument xml;
        using (var package = Package.Open(full, FileMode.Open, FileAccess.Read))
        {
            if (!package.PartExists(DocumentPart))
                return Task.FromResult(ToolResult.Fail("word/document.xml 을 찾을 수 없습니다(.docx 형식이 아닐 수 있음)."));

            using var stream = package.GetPart(DocumentPart).GetStream(FileMode.Open, FileAccess.Read);
            xml = XDocument.Load(stream);
        }

        var sb = new StringBuilder();
        var body = xml.Root?.Element(W + "body");
        if (body is not null)
        {
            foreach (var para in body.Descendants(W + "p"))
            {
                ct.ThrowIfCancellationRequested();
                AppendParagraph(sb, para);
                sb.Append('\n');
                if (sb.Length >= cap) break;
            }
        }

        var text = sb.Length > cap ? sb.ToString(0, cap) : sb.ToString().TrimEnd('\n');
        return Task.FromResult(ToolResult.Json(new { path, text, truncated = sb.Length > cap }));
    }

    /// <summary>한 문단(w:p) 안의 텍스트 런(w:t)·탭(w:tab)·줄바꿈(w:br)을 순서대로 이어붙인다.</summary>
    private static void AppendParagraph(StringBuilder sb, XElement para)
    {
        foreach (var node in para.Descendants())
        {
            if (node.Name == W + "t") sb.Append(node.Value);
            else if (node.Name == W + "tab") sb.Append('\t');
            else if (node.Name == W + "br") sb.Append('\n');
        }
    }
}
