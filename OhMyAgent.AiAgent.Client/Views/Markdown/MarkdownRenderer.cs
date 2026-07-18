using System;
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

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel) return;

        panel.Children.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text)) return;

        foreach (var block in MarkdownParser.Parse(text))
            panel.Children.Add(BuildBlock(block));
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
