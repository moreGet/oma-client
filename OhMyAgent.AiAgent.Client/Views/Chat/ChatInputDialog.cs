using System;
using System.Windows;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace OhMyAgent.AiAgent.Client.Views.Chat;

/// <summary>
/// 메신저 전용 간단 입력 모달(다크 테마, 기존 토큰 재사용). 새 대화/그룹 생성·메시지 인라인 편집 등
/// 작은 텍스트 입력에 사용한다. BCL/WPF only — 신규 스타일/색 정의 없음(WindowBg/DarkTextBox/PrimaryButton/OutlineButton).
/// 취소 시 <c>null</c> 반환.
/// </summary>
internal static class ChatInputDialog
{
    /// <summary>단일 행 입력. 확인 시 입력값(빈 문자열 허용 여부는 호출자 판단), 취소 시 null.</summary>
    public static string? Prompt(Window? owner, string title, string message, string defaultValue = "",
                                 bool multiline = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = TryBrush(owner, "WindowBg") ?? Brushes.Black,
        };

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = TryBrush(owner, "TextSecondary") ?? Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var input = new TextBox
        {
            Text = defaultValue,
            Style = TryStyle(owner, "DarkTextBox"),
            MinHeight = multiline ? 80 : 34,
            FontSize = 13,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        root.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var buttonPadding = new Thickness(16, 6, 16, 6);
        var ok = new Button
        {
            Content = "확인",
            Style = TryStyle(owner, "PrimaryButton"),
            Padding = buttonPadding,
            FontSize = 12,
            IsDefault = !multiline,   // 멀티라인은 Enter 가 줄바꿈이라 기본 버튼 비활성
            Margin = new Thickness(0, 0, 8, 0),
        };
        var cancel = new Button
        {
            Content = "취소",
            Style = TryStyle(owner, "OutlineButton"),
            Padding = buttonPadding,
            FontSize = 12,
            IsCancel = true,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        dialog.Content = root;

        var confirmed = false;
        ok.Click += (_, _) => { confirmed = true; dialog.DialogResult = true; };
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

        dialog.ShowDialog();
        return confirmed ? input.Text : null;
    }

    private static Brush? TryBrush(FrameworkElement? scope, string key)
    {
        try { return (scope?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Brush; }
        catch { return null; }
    }

    private static Style? TryStyle(FrameworkElement? scope, string key)
    {
        try { return (scope?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Style; }
        catch { return null; }
    }
}
