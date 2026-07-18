using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OhMyAgent.AiAgent.Client.Views.Markdown;

/// <summary>
/// 경량 마크다운 파서(순수 함수 — WPF 비의존). 에이전트 응답에 흔한 부분집합만 지원한다:
/// 펜스 코드(```), ATX 헤더(#..######), 순서/비순서 목록, 그리고 인라인 **굵게**/*기울임*/`코드`.
///
/// 왜 부분집합인가: 완전한 CommonMark 는 과하고, 스트리밍 중 매 플러시마다 재파싱해야 하므로 단순·빠름이 우선이다.
/// 스트리밍 안전: 닫히지 않은 코드 펜스는 문서 끝까지 코드 블록으로 취급한다(입력 도중이라도 깨지지 않는다).
///
/// 렌더 로직과 분리해 파싱만 순수 함수로 두었다 — 인라인 처리가 실수하기 쉬운 부분이라 단위 테스트로 고정한다.
/// </summary>
public static class MarkdownParser
{
    private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex UnorderedRx = new(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedRx = new(@"^\s*\d+[.)]\s+(.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(text))
            return blocks;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // ── 펜스 코드 블록 ──
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var lang = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                i++; // 닫는 ``` 소비(EOF 면 무해).
                blocks.Add(new MdCode(lang, code.ToString()));
                continue;
            }

            // ── 헤더 ──
            var h = HeadingRx.Match(line);
            if (h.Success)
            {
                blocks.Add(new MdHeading(h.Groups[1].Value.Length, ParseInlines(h.Groups[2].Value)));
                i++;
                continue;
            }

            // ── 목록(연속 항목을 하나로 모은다) ──
            if (UnorderedRx.IsMatch(line) || OrderedRx.IsMatch(line))
            {
                var ordered = OrderedRx.IsMatch(line);
                var items = new List<IReadOnlyList<MdRun>>();
                while (i < lines.Length)
                {
                    var u = UnorderedRx.Match(lines[i]);
                    var o = OrderedRx.Match(lines[i]);
                    if (ordered && o.Success) items.Add(ParseInlines(o.Groups[1].Value));
                    else if (!ordered && u.Success) items.Add(ParseInlines(u.Groups[1].Value));
                    else break;
                    i++;
                }
                blocks.Add(new MdList(ordered, items));
                continue;
            }

            // ── 빈 줄 ──
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }

            // ── 문단(빈 줄/특수 블록 전까지 이어붙인다) ──
            var para = new StringBuilder();
            while (i < lines.Length)
            {
                var l = lines[i];
                if (l.Trim().Length == 0) break;
                if (l.TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
                if (HeadingRx.IsMatch(l) || UnorderedRx.IsMatch(l) || OrderedRx.IsMatch(l)) break;
                if (para.Length > 0) para.Append('\n');
                para.Append(l);
                i++;
            }
            blocks.Add(new MdParagraph(ParseInlines(para.ToString())));
        }

        return blocks;
    }

    /// <summary>
    /// 인라인 서식을 런 목록으로 분해한다. `코드` 를 먼저 잘라낸다 — 코드 안의 *·** 는 서식이 아니라 리터럴이다.
    /// 짝이 안 맞는 마커는 리터럴로 남긴다(스트리밍 도중 반쪽 마커가 깨지지 않게).
    /// </summary>
    public static IReadOnlyList<MdRun> ParseInlines(string text)
    {
        var runs = new List<MdRun>();
        if (string.IsNullOrEmpty(text))
            return runs;

        var i = 0;
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length > 0) { runs.Add(new MdRun(literal.ToString(), MdStyle.None)); literal.Clear(); }
        }

        while (i < text.Length)
        {
            var c = text[i];

            // 인라인 코드 — 최우선, 내부 마커는 리터럴.
            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushLiteral();
                    runs.Add(new MdRun(text[(i + 1)..end], MdStyle.Code));
                    i = end + 1;
                    continue;
                }
            }
            // **굵게**
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    FlushLiteral();
                    runs.Add(new MdRun(text[(i + 2)..end], MdStyle.Bold));
                    i = end + 2;
                    continue;
                }
            }
            // *기울임*
            else if (c == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i)
                {
                    FlushLiteral();
                    runs.Add(new MdRun(text[(i + 1)..end], MdStyle.Italic));
                    i = end + 1;
                    continue;
                }
            }

            literal.Append(c);
            i++;
        }

        FlushLiteral();
        return runs;
    }
}
