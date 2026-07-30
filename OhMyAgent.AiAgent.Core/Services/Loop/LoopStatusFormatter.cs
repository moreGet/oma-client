using System;
using System.Text;
using OhMyAgent.AiAgent.Client.Models.Loop;

namespace OhMyAgent.AiAgent.Client.Services.Loop;

/// <summary>
/// 루프 상태의 사람용 표기. GUI 상단바와 헤드리스 stderr 가 같은 문구를 쓰도록 한 곳에 모은다 —
/// 표기가 갈리면 "왜 GUI 랑 CLI 가 다른 말을 하지" 라는 버그 리포트가 된다.
/// </summary>
public static class LoopStatusFormatter
{
    /// <summary>상단바/stderr 공용 1줄 표기. 미실행 상태는 빈 문자열(= 표시하지 않음).</summary>
    public static string Describe(LoopStatusSnapshot s)
    {
        if (s is null) return "";

        return s.State switch
        {
            LoopState.RunningTurn => $"루프 실행 중 · 반복 {s.Iteration}/{s.MaxIterations} · 실행 중",
            LoopState.Waiting     => $"루프 실행 중 · 반복 {s.Iteration}/{s.MaxIterations} · 다음 실행까지 {Remaining(s)}",
            LoopState.Stopping    => "루프 중지 중…",
            _                     => "",
        };
    }

    /// <summary>/loop status 의 여러 줄 상세 표기.</summary>
    public static string DescribeDetailed(LoopStatusSnapshot s)
    {
        if (s is null || s.State is LoopState.Idle)
            return "반복 실행 중이 아닙니다. /loop <간격> <프롬프트> 로 시작하세요.";

        if (s.State is LoopState.Stopped)
            return $"반복 실행이 종료되었습니다 — {StopReasonText(s.StopReason)} (총 {s.Iteration}회)";

        var sb = new StringBuilder();
        sb.AppendLine(Describe(s));
        sb.AppendLine(s.Mode == LoopMode.FixedInterval
            ? $"  모드: 고정 간격 {LoopIntervalParser.Format(s.Interval ?? TimeSpan.Zero)}"
            : "  모드: 자율 페이싱(모델이 다음 실행 시점을 정함)");
        sb.AppendLine($"  프롬프트: {s.Prompt}");
        if (s.NextRunAt is { } next && s.State is LoopState.Waiting)
            sb.AppendLine($"  다음 실행: {next.ToLocalTime():HH:mm:ss}");
        if (s.ConsecutiveFailures > 0)
            sb.AppendLine($"  연속 실패: {s.ConsecutiveFailures}/{LoopPolicy.MaxConsecutiveFailures}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>종료 사유의 사용자용 문구. 자동 중지(상한·연속 실패)를 사용자 중지와 구분해 보여준다.</summary>
    public static string StopReasonText(LoopStopReason reason) => reason switch
    {
        LoopStopReason.UserStopped          => "사용자 중지",
        LoopStopReason.MaxIterationsReached => "최대 반복 횟수 도달",
        LoopStopReason.RepeatedFailures     => "연속 실패로 자동 중지",
        LoopStopReason.ModelRequested       => "모델이 완료로 표시",
        LoopStopReason.Disposed             => "세션 정리",
        LoopStopReason.HostShutdown         => "앱 종료",
        _                                   => "종료",
    };

    private static string Remaining(LoopStatusSnapshot s)
        => s.Remaining is { } r && r > TimeSpan.Zero ? LoopIntervalParser.Format(r) : "곧";
}
