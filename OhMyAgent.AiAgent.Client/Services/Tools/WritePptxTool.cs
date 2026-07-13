using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using OhMyAgent.AiAgent.Client.Models;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 텍스트로 PowerPoint .pptx 를 생성한다. OpenXML SDK(ClosedXML 전이 의존성 — 신규 런타임 부담 없음)로
/// 유효한 슬라이드 마스터/레이아웃/테마를 포함한 덱을 구성한다.
/// </summary>
public sealed class WritePptxTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{
          "destination":{"type":"string","description":"생성할 .pptx 경로(워크스페이스 내부)"},
          "slides":{"type":"array","items":{"type":"object","properties":{
            "title":{"type":"string"},
            "body":{"type":"string","description":"본문(줄바꿈=문단/불릿)"}
          }}},
          "overwrite":{"type":"boolean"}
        },"required":["destination","slides"]}
        """);

    public string Name => "write_pptx";
    public string Description => "Create a PowerPoint .pptx from text. Each slide has an optional title and a body (newlines become separate paragraphs/bullets). Produces a valid deck (master/layout/theme included).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var destination = ToolSchemas.GetString(args, "destination");
        if (string.IsNullOrWhiteSpace(destination))
            return ToolResult.Fail("destination(.pptx 경로)이 필요합니다.");
        if (!args.TryGetProperty("slides", out var slidesEl) || slidesEl.ValueKind != JsonValueKind.Array || slidesEl.GetArrayLength() == 0)
            return ToolResult.Fail("slides 배열(최소 1개)이 필요합니다.");
        var overwrite = ToolSchemas.GetBool(args, "overwrite");

        var slides = slidesEl.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => (Title: ToolSchemas.GetString(e, "title") ?? "", Body: ToolSchemas.GetString(e, "body") ?? ""))
            .ToList();

        var destFull = ctx.Workspace.ResolvePath(destination);
        if (File.Exists(destFull) && !overwrite)
            return ToolResult.Fail($"이미 존재합니다(overwrite=false): {destination}");

        return await Task.Run(() =>
        {
            var parent = Path.GetDirectoryName(destFull);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(destFull)) File.Delete(destFull);

            using var doc = PresentationDocument.Create(destFull, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
            var presPart = doc.AddPresentationPart();
            presPart.Presentation = new P.Presentation();

            // master → theme + layout
            var masterPart = presPart.AddNewPart<SlideMasterPart>();
            var themePart = masterPart.AddNewPart<ThemePart>();
            using (var tw = new StreamWriter(themePart.GetStream(FileMode.Create))) tw.Write(ThemeXml);

            var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
            layoutPart.SlideLayout = BuildLayout();

            masterPart.SlideMaster = BuildMaster(masterPart.GetIdOfPart(layoutPart));

            // slides
            var sldIdList = new P.SlideIdList();
            var slideId = 256U;
            foreach (var (title, body) in slides)
            {
                ct.ThrowIfCancellationRequested();
                var slidePart = presPart.AddNewPart<SlidePart>();
                slidePart.Slide = BuildSlide(title, body);
                slidePart.AddPart(layoutPart);
                sldIdList.Append(new P.SlideId { Id = slideId++, RelationshipId = presPart.GetIdOfPart(slidePart) });
            }

            presPart.Presentation.Append(
                new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648U, RelationshipId = presPart.GetIdOfPart(masterPart) }),
                sldIdList,
                new P.SlideSize { Cx = 9144000, Cy = 6858000 },
                new P.NotesSize { Cx = 6858000, Cy = 9144000 });
            presPart.Presentation.Save();

            var size = new FileInfo(destFull).Length;
            return ToolResult.Json(new { destination, slides = slides.Count, bytes = size });
        }, ct).ConfigureAwait(false);
    }

    private static P.SlideMaster BuildMaster(string layoutRelId) => new(
        new P.CommonSlideData(EmptyShapeTree()),
        new P.ColorMap
        {
            Background1 = D.ColorSchemeIndexValues.Light1,
            Text1 = D.ColorSchemeIndexValues.Dark1,
            Background2 = D.ColorSchemeIndexValues.Light2,
            Text2 = D.ColorSchemeIndexValues.Dark2,
            Accent1 = D.ColorSchemeIndexValues.Accent1,
            Accent2 = D.ColorSchemeIndexValues.Accent2,
            Accent3 = D.ColorSchemeIndexValues.Accent3,
            Accent4 = D.ColorSchemeIndexValues.Accent4,
            Accent5 = D.ColorSchemeIndexValues.Accent5,
            Accent6 = D.ColorSchemeIndexValues.Accent6,
            Hyperlink = D.ColorSchemeIndexValues.Hyperlink,
            FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink
        },
        new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = layoutRelId }));

    private static P.SlideLayout BuildLayout() => new(
        new P.CommonSlideData(EmptyShapeTree()),
        new P.ColorMapOverride(new D.MasterColorMapping()))
    { Type = P.SlideLayoutValues.Blank };

    private static P.Slide BuildSlide(string title, string body)
    {
        var tree = EmptyShapeTree();
        tree.Append(MakeTextShape(2, "Title", title, isTitle: true));
        tree.Append(MakeTextShape(3, "Body", body, isTitle: false));
        return new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new D.MasterColorMapping()));
    }

    private static P.ShapeTree EmptyShapeTree() => new(
        new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new D.TransformGroup()));

    private static P.Shape MakeTextShape(uint id, string name, string text, bool isTitle)
    {
        var body = new P.TextBody(new D.BodyProperties(), new D.ListStyle());
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var any = false;
        foreach (var line in lines)
        {
            body.Append(new D.Paragraph(new D.Run(new D.RunProperties { Language = "ko-KR" }, new D.Text(line))));
            any = true;
        }
        if (!any) body.Append(new D.Paragraph());

        var placeholder = isTitle
            ? new P.PlaceholderShape { Type = P.PlaceholderValues.Title }
            : new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1 };

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new D.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(placeholder)),
            new P.ShapeProperties(),
            body);
    }

    private const string ThemeXml =
        """
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office Theme"><a:themeElements><a:clrScheme name="Office"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2><a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2><a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4><a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6><a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink></a:clrScheme><a:fontScheme name="Office"><a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Office"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>
        """;
}
