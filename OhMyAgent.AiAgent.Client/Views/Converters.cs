using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.ViewModels.Transcript;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace OhMyAgent.AiAgent.Client.Views;

/// <summary>
/// 표시 전용(OneWay) 컨버터의 공통 베이스 — 역변환을 지원하지 않는다.
/// 바인딩이 실수로 TwoWay 로 걸리면 조용히 무시되는 대신 즉시 드러나도록 예외를 던진다.
/// 역변환이 의미 있는 컨버터는 IValueConverter 를 직접 구현한다.
/// </summary>
public abstract class OneWayConverter : IValueConverter
{
    public abstract object Convert(object? value, Type t, object p, CultureInfo c);

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

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
public sealed class BoolToStatusBrushConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => value is true
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))  // #34D399 Connected
            : new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85)); // #FB7185 Disconnected
}

/// <summary>null =&gt; Collapsed, non-null =&gt; Visible. Used for the inline approval card (PendingApproval).</summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Empty/null string =&gt; Collapsed, otherwise Visible.</summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Count &gt; 0 =&gt; Visible (transcript present), 0 =&gt; Collapsed. Used to switch to the transcript view.</summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class CountToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Count == 0 =&gt; Visible (empty transcript ⇒ welcome screen), &gt; 0 =&gt; Collapsed.</summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class EmptyCountToVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => value is int n && n > 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Maps a <see cref="ToolRisk"/> to an accent brush for tool-call card headers.</summary>
[ValueConversion(typeof(ToolRisk), typeof(SolidColorBrush))]
public sealed class ToolRiskToBrushConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
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
}

/// <summary>Maps a <see cref="ToolCallStatus"/> to a status brush (badge background / text).</summary>
[ValueConversion(typeof(ToolCallStatus), typeof(SolidColorBrush))]
public sealed class ToolCallStatusToBrushConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
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
}

/// <summary>Maps a <see cref="ToolCallStatus"/> to a short glyph + label for the status badge.</summary>
[ValueConversion(typeof(ToolCallStatus), typeof(string))]
public sealed class ToolCallStatusToTextConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
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
}

/// <summary>Maps a <see cref="ToolRisk"/> to a Korean label for the risk badge.</summary>
[ValueConversion(typeof(ToolRisk), typeof(string))]
public sealed class ToolRiskToTextConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
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
}

/// <summary>bool(접힘) =&gt; GridLength. true=0(접힘), false=ExpandedWidth(ConverterParameter 폭, 기본 260).</summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public double ExpandedWidth { get; set; } = 260;

    public object Convert(object value, Type t, object parameter, CultureInfo c)
    {
        bool collapsed = value is true;
        if (collapsed) return new GridLength(0);
        double w = ExpandedWidth;
        if (parameter is string ps && double.TryParse(ps, NumberStyles.Any, CultureInfo.InvariantCulture, out var pv))
            w = pv;
        return new GridLength(w);
    }

    public object ConvertBack(object value, Type t, object parameter, CultureInfo c)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>double × parameter(fraction). 컨테이너 ActualWidth 에 곱해 반응형 MaxWidth 산출.</summary>
public sealed class MultiplyConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
    {
        if (value is double d && !double.IsNaN(d) && d > 0 &&
            double.TryParse(p as string, NumberStyles.Any, CultureInfo.InvariantCulture, out var f))
            return d * f;
        return double.PositiveInfinity; // 컨테이너 폭 미확정 시 제한 없음(잘림 방지).
    }
}

// Chat(사람↔사람 메신저) 전용 컨버터 — 설계서 §1.
// 기존 디자인 토큰과 동일한 RGB 만 사용(신규 색 정의 금지).

/// <summary>long(unix epoch 초) =&gt; 로컬 시각 표시 문자열. 오늘=시각만, 어제="어제", 그 외=날짜.</summary>
[ValueConversion(typeof(long), typeof(string))]
public sealed class UnixSecondsToLocalTimeConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
    {
        if (value is not long secs || secs <= 0) return string.Empty;
        var local = DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime().DateTime;
        var today = DateTime.Today;
        if (local.Date == today)
            return local.ToString("tt h:mm", c);        // 예: "오후 3:21"
        if (local.Date == today.AddDays(-1))
            return "어제";
        if (local.Year == today.Year)
            return local.ToString("M월 d일", c);         // 예: "6월 27일"
        return local.ToString("yyyy.M.d", c);
    }
}

/// <summary>int(안읽음 수) &gt; 0 =&gt; Visible. (기존 CountToVisibility 와 동치 — 의미 명료화용 별칭.)</summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class UnreadCountToBadgeVisibilityConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary><see cref="ChatConnectionState"/> =&gt; 상태 점 Brush(연결=green / 재연결·연결중=amber / 끊김=red).</summary>
[ValueConversion(typeof(ChatConnectionState), typeof(SolidColorBrush))]
public sealed class ChatConnectionStateToBrushConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => new SolidColorBrush(value is ChatConnectionState s
            ? s switch
            {
                ChatConnectionState.Connected    => Color.FromRgb(0x34, 0xD3, 0x99), // ConnectedDot
                ChatConnectionState.Connecting   => Color.FromRgb(0xFB, 0xBF, 0x24), // WarningBrush
                ChatConnectionState.Reconnecting => Color.FromRgb(0xFB, 0xBF, 0x24), // WarningBrush
                _                                => Color.FromRgb(0xFB, 0x71, 0x85), // ErrorDot
            }
            : Color.FromRgb(0xFB, 0x71, 0x85));
}

/// <summary>bool(내 메시지) =&gt; HorizontalAlignment(true=Right / false=Left).</summary>
[ValueConversion(typeof(bool), typeof(HorizontalAlignment))]
public sealed class IsMineToAlignmentConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;
}

/// <summary>bool(내 메시지) =&gt; 버블 Brush(true=ChatMeBubble 파랑 반투명 / false=ChatSurface2).</summary>
[ValueConversion(typeof(bool), typeof(SolidColorBrush))]
public sealed class IsMineToBubbleBrushConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
        => new SolidColorBrush(value is true
            ? Color.FromArgb(0x29, 0x38, 0x8B, 0xFD)  // ChatMeBubbleBrush rgba(56,139,253,.16)
            : Color.FromRgb(0x1B, 0x24, 0x33));        // ChatSurface2Brush #1B2433
}

/// <summary>표시이름 → 아바타 이니셜(첫 글자, 대문자). 빈 값은 "?".</summary>
public sealed class InitialConverter : OneWayConverter
{
    public override object Convert(object? value, Type t, object p, CultureInfo c)
    {
        var s = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return "?";
        return char.ToUpper(s[0], c).ToString();
    }
}
