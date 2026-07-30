using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OhMyAgent.AiAgent.Client.Services.Loop;

/// <summary>
/// `/loop` 의 간격 토큰 파서 + 사람이 읽는 표기. 순수 함수(I/O·시계 비의존).
/// </summary>
public static partial class LoopIntervalParser
{
    // 단위 접미사를 필수로 두는 이유: "/loop 5 분마다 확인" 의 5 를 초로 볼지 분으로 볼지 결정할 근거가 없다.
    // 단위 없는 숫자는 프롬프트의 일부로 넘겨야 사용자가 의도한 문장이 잘리지 않는다.
    [GeneratedRegex(@"^(?<v>\d+(\.\d+)?)(?<u>s|sec|secs|m|min|mins|h|hr|hrs)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>"5m" / "30s" / "1.5h" → TimeSpan. 단위 접미사 필수. 0 이하·형식 불일치는 false.</summary>
    public static bool TryParse(string? token, out TimeSpan interval)
    {
        interval = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var m = TokenPattern().Match(token.Trim());
        if (!m.Success) return false;

        if (!double.TryParse(m.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;
        if (!double.IsFinite(value) || value <= 0) return false;

        var unit = m.Groups["u"].Value.ToLowerInvariant();
        var span = unit switch
        {
            "s" or "sec" or "secs"  => TimeSpan.FromSeconds(value),
            "m" or "min" or "mins"  => TimeSpan.FromMinutes(value),
            _                       => TimeSpan.FromHours(value),
        };

        if (span <= TimeSpan.Zero) return false;
        interval = span;
        return true;
    }

    /// <summary>TimeSpan → 한국어 표기("8초", "2분 14초", "1시간 5분", "24시간").</summary>
    public static string Format(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return "0초";

        // 초 미만은 반올림해 올린다 — "0초 뒤 실행" 이라고 쓰면 이미 지난 것처럼 읽힌다.
        var total = TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));

        if (total.TotalSeconds < 60)
            return $"{(int)total.TotalSeconds}초";

        if (total.TotalMinutes < 60)
        {
            var min = total.Minutes;
            var sec = total.Seconds;
            return sec > 0 ? $"{min}분 {sec}초" : $"{min}분";
        }

        var hours = (int)total.TotalHours;
        var minutes = total.Minutes;
        return minutes > 0 ? $"{hours}시간 {minutes}분" : $"{hours}시간";
    }
}
