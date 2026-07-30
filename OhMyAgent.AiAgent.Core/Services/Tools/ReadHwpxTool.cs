using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 한글 .hwpx(OWPML) 에서 본문 텍스트를 추출한다. .hwpx 는 zip+XML 이라 BCL 만으로 처리(의존성 0).
/// 네임스페이스 버전 차이를 흡수하려 텍스트=LocalName "t", 문단=LocalName "p" 로 무관하게 처리한다.
/// </summary>
public sealed partial class ReadHwpxTool : ITool
{
    private const int MaxChars = 200 * 1024;

    [GeneratedRegex(@"^Contents/section(\d+)\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionRe();

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"max_chars":{"type":"integer"}},"required":["path"]}
        """);

    public string Name => "read_hwpx";
    public string Description => "Extract body text from a Hangul .hwpx (OWPML) file in the workspace. Use for reading and summarizing Korean documents.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (!ToolPaths.TryResolveExistingFile(args, ctx, out var path, out var full, out var error))
            return Task.FromResult(error);

        var cap = ToolSchemas.GetInt(args, "max_chars") ?? MaxChars;

        try
        {
            using var zip = ZipFile.OpenRead(full);
            var sections = zip.Entries
                .Select(e => (e, m: SectionRe().Match(e.FullName)))
                .Where(x => x.m.Success)
                .OrderBy(x => int.Parse(x.m.Groups[1].Value))
                .Select(x => x.e)
                .ToList();

            if (sections.Count == 0)
                return Task.FromResult(ToolResult.Fail("Contents/sectionN.xml 을 찾을 수 없습니다(.hwpx 형식이 아닐 수 있음)."));

            var sb = new StringBuilder();
            var truncated = false;
            foreach (var entry in sections)
            {
                ct.ThrowIfCancellationRequested();
                using var s = entry.Open();
                var doc = XDocument.Load(s);
                foreach (var para in doc.Descendants().Where(e => e.Name.LocalName == "p"))
                {
                    foreach (var t in para.Descendants().Where(e => e.Name.LocalName == "t"))
                        sb.Append(t.Value);
                    sb.Append('\n');
                    if (sb.Length >= cap) { truncated = true; break; }
                }
                if (truncated) break;
            }

            var text = sb.Length > cap ? sb.ToString(0, cap) : sb.ToString().TrimEnd('\n');
            return Task.FromResult(ToolResult.Json(new { path, section_count = sections.Count, text, truncated }));
        }
        catch (InvalidDataException)
        {
            return Task.FromResult(ToolResult.Fail($"유효한 .hwpx(zip) 가 아닙니다: {path}"));
        }
        catch (XmlException ex)
        {
            return Task.FromResult(ToolResult.Fail($"HWPX XML 파싱 실패: {ex.Message}"));
        }
    }
}
