using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 상단바 쿼터 칩/팝업이 바인딩하는 경량(불변) 항목 VM. 한 윈도우(일/주/월)의
/// 표시 상태를 캡슐화한다. <see cref="QuotaWindow"/> 서버 모델로부터 생성된다.
/// </summary>
public sealed class QuotaWindowViewModel
{
    public QuotaWindowViewModel(QuotaWindow window)
    {
        Key = window.Window;
        Label = window.Window switch
        {
            "day"   => "일",
            "week"  => "주",
            "month" => "월",
            _       => window.Window,
        };
        IsUnlimited = window.Unlimited;
        PercentUsed = window.PercentUsed;
        PercentRemaining = window.PercentRemaining;
        PeriodText = window.Period;

        AmountText = window.Unlimited
            ? "무제한"
            : $"{window.Remaining:N0} / {window.Limit:N0} 남음";

        IsConstrained = !window.Unlimited && window.PercentRemaining <= 10;
    }

    /// <summary>원본 window 키: "day"|"week"|"month" (칩 선택이 윈도우를 식별하는 데 사용).</summary>
    public string Key { get; }

    /// <summary>윈도우 한글 라벨: day="일", week="주", month="월".</summary>
    public string Label { get; }

    public bool IsUnlimited { get; }

    /// <summary>0~100 사용률.</summary>
    public double PercentUsed { get; }

    /// <summary>0~100 잔여율.</summary>
    public double PercentRemaining { get; }

    /// <summary>무제한이면 "무제한", 아니면 "{remaining} / {limit} 남음".</summary>
    public string AmountText { get; }

    /// <summary>서버 period 문자열 그대로(예: 2026-06-27 / 2026-W26 / 2026-06).</summary>
    public string PeriodText { get; }

    /// <summary>거의 소진(비무제한 &amp;&amp; 잔여 ≤ 10%) → 경고 표시.</summary>
    public bool IsConstrained { get; }
}
