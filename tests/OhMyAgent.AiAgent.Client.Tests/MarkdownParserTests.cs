using System.Linq;
using OhMyAgent.AiAgent.Client.Views.Markdown;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 마크다운 파서(순수). 렌더는 WPF 라 단위 테스트 못 하지만, 실수하기 쉬운 파싱은 여기서 고정한다.
/// 특히: 스트리밍 중 반쪽 마커/닫히지 않은 펜스가 깨지지 않아야 한다(매 플러시마다 재파싱된다).
/// </summary>
public class MarkdownParserTests
{
    private static string Plain(System.Collections.Generic.IReadOnlyList<MdRun> runs)
        => string.Concat(runs.Select(r => r.Text));

    // ── 블록 ──

    [Fact]
    public void Parse_FencedCodeBlock()
    {
        var blocks = MarkdownParser.Parse("설명\n```csharp\nvar x = 1;\n```\n끝");

        Assert.Collection(blocks,
            b => Assert.IsType<MdParagraph>(b),
            b => { var c = Assert.IsType<MdCode>(b); Assert.Equal("csharp", c.Language); Assert.Equal("var x = 1;", c.Text); },
            b => Assert.IsType<MdParagraph>(b));
    }

    [Fact]
    public void Parse_CodeBlockPreservesInnerMarkersAsLiteral()
    {
        // 코드 안의 ** 는 서식이 아니라 그대로여야 한다.
        var code = Assert.IsType<MdCode>(MarkdownParser.Parse("```\na ** b * c `d`\n```")[0]);
        Assert.Equal("a ** b * c `d`", code.Text);
    }

    [Fact]
    public void Parse_UnclosedFence_IsStreamingSafe()
    {
        // 스트리밍 도중 아직 안 닫힌 펜스 — 문서 끝까지 코드로 취급(깨지지 않는다).
        var blocks = MarkdownParser.Parse("```python\nprint(1)\nprint(2)");
        var code = Assert.IsType<MdCode>(Assert.Single(blocks));
        Assert.Equal("print(1)\nprint(2)", code.Text);
    }

    [Theory]
    [InlineData("# 제목", 1)]
    [InlineData("### 소제목", 3)]
    [InlineData("###### 최소", 6)]
    public void Parse_Heading(string input, int level)
    {
        var h = Assert.IsType<MdHeading>(MarkdownParser.Parse(input)[0]);
        Assert.Equal(level, h.Level);
    }

    [Fact]
    public void Parse_UnorderedList()
    {
        var list = Assert.IsType<MdList>(MarkdownParser.Parse("- 하나\n- 둘\n- 셋")[0]);
        Assert.False(list.Ordered);
        Assert.Equal(3, list.Items.Count);
        Assert.Equal("둘", Plain(list.Items[1]));
    }

    [Fact]
    public void Parse_OrderedList()
    {
        var list = Assert.IsType<MdList>(MarkdownParser.Parse("1. 첫째\n2. 둘째")[0]);
        Assert.True(list.Ordered);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void Parse_ParagraphSeparatedByBlankLine()
    {
        var blocks = MarkdownParser.Parse("첫 문단\n계속\n\n둘째 문단");
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.IsType<MdParagraph>(b));
    }

    // ── 인라인 ──

    [Fact]
    public void Inlines_BoldItalicCode()
    {
        var runs = MarkdownParser.ParseInlines("**굵게** 와 *기울임* 과 `코드`");

        Assert.Contains(runs, r => r.Text == "굵게" && r.Style == MdStyle.Bold);
        Assert.Contains(runs, r => r.Text == "기울임" && r.Style == MdStyle.Italic);
        Assert.Contains(runs, r => r.Text == "코드" && r.Style == MdStyle.Code);
    }

    [Fact]
    public void Inlines_CodeSuppressesInnerFormatting()
    {
        // 인라인 코드 안의 ** 는 리터럴.
        var runs = MarkdownParser.ParseInlines("`a ** b`");
        var code = Assert.Single(runs);
        Assert.Equal("a ** b", code.Text);
        Assert.Equal(MdStyle.Code, code.Style);
    }

    [Theory]
    [InlineData("**닫히지 않은")]     // 반쪽 굵게
    [InlineData("*반쪽 기울임")]
    [InlineData("`반쪽 코드")]
    public void Inlines_UnmatchedMarkers_StayLiteral(string input)
    {
        // 스트리밍 도중 반쪽 마커는 리터럴로 — 서식이 튀거나 텍스트가 사라지면 안 된다.
        var runs = MarkdownParser.ParseInlines(input);
        Assert.Equal(input, Plain(runs));
        Assert.All(runs, r => Assert.Equal(MdStyle.None, r.Style));
    }

    [Fact]
    public void Inlines_PlainTextIsSingleRun()
    {
        var runs = MarkdownParser.ParseInlines("서식 없는 일반 텍스트");
        var run = Assert.Single(runs);
        Assert.Equal(MdStyle.None, run.Style);
    }

    [Fact]
    public void Parse_EmptyAndNull()
    {
        Assert.Empty(MarkdownParser.Parse(""));
        Assert.Empty(MarkdownParser.Parse(null));
        Assert.Empty(MarkdownParser.ParseInlines(""));
    }

    [Fact]
    public void Parse_PlainTextNoMarkdown_RoundTrips()
    {
        // 마크다운이 전혀 없는 응답도 온전히 문단으로 나와야 한다(텍스트 유실 없음).
        var blocks = MarkdownParser.Parse("그냥 평범한 답변입니다. 특별한 서식 없음.");
        var para = Assert.IsType<MdParagraph>(Assert.Single(blocks));
        Assert.Equal("그냥 평범한 답변입니다. 특별한 서식 없음.", Plain(para.Runs));
    }
}
