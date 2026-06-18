using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.ViewModels.Transcript;
using Color = System.Windows.Media.Color;

namespace OhMyAgent.AiAgent.Client.Views;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is Visibility.Visible;
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is not Visibility.Visible;
}

[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is not true;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is not true;
}

[ValueConversion(typeof(bool), typeof(SolidColorBrush))]
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))  // #34D399 Connected
            : new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85)); // #FB7185 Disconnected

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>null =&gt; Collapsed, non-null =&gt; Visible. Used for the inline approval card (PendingApproval).</summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Empty/null string =&gt; Collapsed, otherwise Visible.</summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ToolRisk"/> to an accent brush for tool-call card headers.</summary>
[ValueConversion(typeof(ToolRisk), typeof(SolidColorBrush))]
public sealed class ToolRiskToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => new SolidColorBrush(value is ToolRisk risk
            ? risk switch
            {
                ToolRisk.ReadOnly    => Color.FromRgb(0x9C, 0xA3, 0xB4), // neutral grey
                ToolRisk.Write       => Color.FromRgb(0xFB, 0xBF, 0x24), // amber
                ToolRisk.Execute     => Color.FromRgb(0x7C, 0x5C, 0xFF), // violet accent
                ToolRisk.Destructive => Color.FromRgb(0xFB, 0x71, 0x85), // red
                _                    => Color.FromRgb(0x9C, 0xA3, 0xB4),
            }
            : Color.FromRgb(0x8B, 0x94, 0x9E));

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ToolCallStatus"/> to a status brush (badge background / text).</summary>
[ValueConversion(typeof(ToolCallStatus), typeof(SolidColorBrush))]
public sealed class ToolCallStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => new SolidColorBrush(value is ToolCallStatus status
            ? status switch
            {
                ToolCallStatus.Running          => Color.FromRgb(0x8F, 0x73, 0xFF), // violet
                ToolCallStatus.AwaitingApproval => Color.FromRgb(0xFB, 0xBF, 0x24), // amber
                ToolCallStatus.Succeeded        => Color.FromRgb(0x34, 0xD3, 0x99), // green
                ToolCallStatus.Failed           => Color.FromRgb(0xFB, 0x71, 0x85), // red
                ToolCallStatus.Denied           => Color.FromRgb(0xFB, 0x71, 0x85), // red
                _                               => Color.FromRgb(0x9C, 0xA3, 0xB4),
            }
            : Color.FromRgb(0x8B, 0x94, 0x9E));

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ToolCallStatus"/> to a short glyph + label for the status badge.</summary>
[ValueConversion(typeof(ToolCallStatus), typeof(string))]
public sealed class ToolCallStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is ToolCallStatus status
            ? status switch
            {
                ToolCallStatus.Running          => "● 실행 중",
                ToolCallStatus.AwaitingApproval => "⏳ 승인 대기",
                ToolCallStatus.Succeeded        => "✓ 완료",
                ToolCallStatus.Failed           => "✕ 실패",
                ToolCallStatus.Denied           => "⊘ 거부됨",
                _                               => string.Empty,
            }
            : string.Empty;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ToolRisk"/> to a Korean label for the risk badge.</summary>
[ValueConversion(typeof(ToolRisk), typeof(string))]
public sealed class ToolRiskToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is ToolRisk risk
            ? risk switch
            {
                ToolRisk.ReadOnly    => "읽기",
                ToolRisk.Write       => "쓰기",
                ToolRisk.Execute     => "실행",
                ToolRisk.Destructive => "위험",
                _                    => risk.ToString(),
            }
            : string.Empty;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>true =&gt; Collapsed, false =&gt; Visible (inverse of BoolToVisibility), for "show when NOT".</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class EnumEqualsConverter : IValueConverter
{
    // value == parameter (enum name compare). Used for radio-style ComboBox warnings.
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is not null && p is not null &&
           string.Equals(value.ToString(), p.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}
