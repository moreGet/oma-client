using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
// UseWindowsForms=true 라 System.Drawing/System.Windows.Forms 와 이름이 겹친다 — WPF 타입을 명시적으로 고른다.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Application = System.Windows.Application;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace OhMyAgent.AiAgent.Client.Views.Markdown;

/// <summary>
/// 마크다운 문자열을 WPF 블록 요소로 렌더하는 첨부 속성. <c>md:Markdown.Source</c> 를 Panel(주로 StackPanel)에 걸면,
/// 값이 바뀔 때마다 자식을 다시 만든다. <see cref="MarkdownParser"/>(순수) 로 파싱하고 여기서 WPF 로만 옮긴다.
///
/// 왜 첨부 속성 + Panel 인가: TextBlock 은 인라인만 담아 코드 블록을 별도 시각/폰트/선택 가능 박스로 못 만든다.
/// Panel 에 블록 요소(문단 TextBlock, 코드 Border+TextBox 등)를 쌓아야 코딩 에이전트 응답이 제대로 보인다.
///
/// 리소스 의존: TextPrimary/TextMuted/Surface2Bg/BorderBrush/MonoFont 토큰(App 리소스에 존재). 없으면 폴백.
/// </summary>
public static class Markdown
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(Markdown),
        new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject o, string? value) => o.SetValue(SourceProperty, value);
    public static string? GetSource(DependencyObject o) => (string?)o.GetValue(SourceProperty);

    /// <summary>직전에 렌더한 블록 목록 — 다음 갱신에서 공통 접두를 찾아 바뀐 부분만 다시 만든다.</summary>
    private static readonly DependencyProperty RenderedProperty = DependencyProperty.RegisterAttached(
        "Rendered", typeof(IReadOnlyList<MdBlock>), typeof(Markdown), new PropertyMetadata(null));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel) return;

        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text))
        {
            panel.Children.Clear();
            panel.SetValue(RenderedProperty, null);
            return;
        }

        var blocks = MarkdownParser.Parse(text);
        var reusable = ReusablePrefixLength(panel, blocks);

        for (var i = panel.Children.Count - 1; i >= reusable; i--)
            panel.Children.RemoveAt(i);

        for (var i = reusable; i < blocks.Count; i++)
            panel.Children.Add(BuildBlock(blocks[i]));

        panel.SetValue(RenderedProperty, blocks);
    }

    /// <summary>
    /// 앞에서부터 그대로 둬도 되는 자식 개수. 직전 렌더 결과와 이번 블록이 같은 구간의 길이다.
    ///
    /// 스트리밍 중엔 텍스트가 뒤로만 자라므로 앞쪽 블록은 그대로다. 전부 다시 만들면 갱신마다
    /// O(누적 길이) 라 응답이 길어질수록 눈에 띄게 느려진다(40KB 응답 = 최종 928블록을 그리려고
    /// 누계 18,636블록 생성). 처음 달라진 지점부터만 교체하면 갱신당 보통 1블록이면 끝난다.
    ///
    /// 첫 렌더이거나 자식이 밖에서 바뀌었으면 0 — 호출부의 제거 루프가 전부 걷어내 전체 재구성이 된다.
    /// </summary>
    private static int ReusablePrefixLength(Panel panel, IReadOnlyList<MdBlock> blocks)
    {
        if (panel.GetValue(RenderedProperty) is not IReadOnlyList<MdBlock> previous ||
            panel.Children.Count != previous.Count)
            return 0;

        var max = Math.Min(previous.Count, blocks.Count);
        var reusable = 0;
        while (reusable < max && SameBlock(previous[reusable], blocks[reusable]))
            reusable++;
        return reusable;
    }

    // ── 블록 동등 비교 ──
    //
    // record 기본 동등성은 IReadOnlyList 멤버를 참조로 비교해 매 파싱마다 무조건 false 가 된다.
    // 재사용 판정에 쓰려면 내용으로 비교해야 하므로 여기서 구조 비교를 편다.

    private static bool SameBlock(MdBlock a, MdBlock b) => (a, b) switch
    {
        (MdCode x, MdCode y)           => x == y,   // (string, string) 이라 record 값 비교로 충분
        (MdHeading x, MdHeading y)     => x.Level == y.Level && SameRuns(x.Runs, y.Runs),
        (MdParagraph x, MdParagraph y) => SameRuns(x.Runs, y.Runs),
        (MdList x, MdList y)           => x.Ordered == y.Ordered && SameItems(x.Items, y.Items),
        _                              => false,
    };

    private static bool SameRuns(IReadOnlyList<MdRun> a, IReadOnlyList<MdRun> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;   // MdRun 은 (string, MdStyle) — record 값 비교
        return true;
    }

    private static bool SameItems(IReadOnlyList<IReadOnlyList<MdRun>> a, IReadOnlyList<IReadOnlyList<MdRun>> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!SameRuns(a[i], b[i])) return false;
        return true;
    }

    // ── 리소스 헬퍼(App 리소스에서 토큰을 끌어오고, 없으면 안전한 폴백) ──

    private static Brush Brush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily Mono()
        => Application.Current?.TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas, Courier New");

    private static double FontSize(string key, double fallback)
        => Application.Current?.TryFindResource(key) is double v ? v : fallback;

    // ── 블록 ──

    private static UIElement BuildBlock(MdBlock block) => block switch
    {
        MdCode code       => BuildCode(code),
        MdHeading heading => BuildHeading(heading),
        MdList list       => BuildList(list),
        MdParagraph para  => BuildParagraph(para.Runs, FontSize("FontSizeBody", 14)),
        _                 => new TextBlock(),
    };

    private static UIElement BuildParagraph(System.Collections.Generic.IReadOnlyList<MdRun> runs, double fontSize)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextPrimary", Color.FromRgb(0xE6, 0xE8, 0xF0)),
            FontSize = fontSize,
            Margin = new Thickness(0, 2, 0, 2),
        };
        AppendInlines(tb.Inlines, runs);
        return tb;
    }

    private static UIElement BuildHeading(MdHeading heading)
    {
        // #→큰, ######→작은. 본문보다 확실히 크고 굵게.
        var size = heading.Level switch { 1 => 20.0, 2 => 18.0, 3 => 16.0, _ => 15.0 };
        var tb = (TextBlock)BuildParagraph(heading.Runs, size);
        tb.FontWeight = FontWeights.SemiBold;
        tb.Margin = new Thickness(0, 8, 0, 4);
        return tb;
    }

    private static UIElement BuildList(MdList list)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 2, 0, 2) };
        for (var idx = 0; idx < list.Items.Count; idx++)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = list.Ordered ? $"{idx + 1}." : "•",
                Foreground = Brush("TextMuted", Color.FromRgb(0x93, 0xA1, 0xB5)),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 18,
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var body = (TextBlock)BuildParagraph(list.Items[idx], FontSize("FontSizeBody", 14));
            body.Margin = new Thickness(0);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);

            panel.Children.Add(row);
        }
        return panel;
    }

    private static UIElement BuildCode(MdCode code)
    {
        // 읽기 전용 TextBox — 선택·복사가 공짜로 되고, 가로 스크롤로 긴 줄도 안 깨진다.
        var box = new TextBox
        {
            Text = code.Text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brush("TextPrimary", Color.FromRgb(0xE6, 0xE8, 0xF0)),
            FontFamily = Mono(),
            FontSize = FontSize("FontSizeSmall", 12.5),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsInactiveSelectionHighlightEnabled = true,
        };

        return new Border
        {
            Background = Brush("Surface2Bg", Color.FromRgb(0x1B, 0x24, 0x33)),
            BorderBrush = Brush("BorderBrush", Color.FromRgb(0x26, 0x30, 0x3F)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 5, 0, 5),
            Child = box,
        };
    }

    // ── 인라인 ──

    private static void AppendInlines(InlineCollection target, System.Collections.Generic.IReadOnlyList<MdRun> runs)
    {
        foreach (var run in runs)
        {
            if (run.Style.HasFlag(MdStyle.Code))
            {
                // 인라인 코드: mono + 옅은 배경으로 본문과 구분.
                target.Add(new Run(run.Text)
                {
                    FontFamily = Mono(),
                    Background = Brush("Surface2Bg", Color.FromRgb(0x1B, 0x24, 0x33)),
                    Foreground = Brush("TextPrimary", Color.FromRgb(0xE6, 0xE8, 0xF0)),
                });
                continue;
            }

            Inline inline = new Run(run.Text);
            if (run.Style.HasFlag(MdStyle.Bold)) inline = new Bold(inline);
            if (run.Style.HasFlag(MdStyle.Italic)) inline = new Italic(inline);
            target.Add(inline);
        }
    }
}
