using System;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services.Loop;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 폭주 방지의 심장. 여기 통과 못 하는 변경은 곧 "사용자가 멈추지 못하는 루프"를 뜻한다 —
/// 클램프가 뚫리면 초당 요청이 나가고, 판정 우선순위가 뒤집히면 실패하는 루프가 영원히 재시도한다.
/// </summary>
public class LoopPolicyTests
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    // ── 클램프 ──

    [Fact]
    public void ClampInterval_EnforcesBothEnds()
    {
        Assert.Equal(LoopPolicy.MinInterval, LoopPolicy.ClampInterval(TimeSpan.FromSeconds(1)));
        Assert.Equal(FiveMinutes, LoopPolicy.ClampInterval(FiveMinutes));
        Assert.Equal(LoopPolicy.MaxInterval, LoopPolicy.ClampInterval(TimeSpan.FromHours(48)));
    }

    [Fact]
    public void ClampAutonomousDelay_EnforcesBothEnds()
    {
        Assert.Equal(LoopPolicy.MinAutonomousDelay, LoopPolicy.ClampAutonomousDelay(1));
        Assert.Equal(FiveMinutes, LoopPolicy.ClampAutonomousDelay(300));
        Assert.Equal(LoopPolicy.MaxAutonomousDelay, LoopPolicy.ClampAutonomousDelay(99999));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ClampAutonomousDelay_RejectsNonFinite(double value)
    {
        // 모델 출력은 신뢰하지 않는다 — NaN 이 TimeSpan 으로 흘러들면 대기가 즉시 끝나 폭주한다.
        Assert.Equal(LoopPolicy.DefaultAutonomousDelay, LoopPolicy.ClampAutonomousDelay(value));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(50, 50)]
    [InlineData(99999, LoopPolicy.HardMaxIterations)]
    public void ClampMaxIterations_HasHardCeiling(int input, int expected)
        => Assert.Equal(expected, LoopPolicy.ClampMaxIterations(input));

    // ── Decide 판정 우선순위 (이 순서가 곧 계약이다) ──

    [Fact]
    public void Decide_RepeatedFailures_BeatsEverything()
    {
        // 상한에 한참 못 미치고 모델이 "계속 돌자"고 해도 연속 실패가 이긴다.
        var d = LoopPolicy.Decide(
            LoopMode.Autonomous, null, completedIterations: 1, maxIterations: 50,
            consecutiveFailures: LoopPolicy.MaxConsecutiveFailures, lastTurnSucceeded: false,
            new WakeupRequest(TimeSpan.FromMinutes(1), null, false, DateTimeOffset.UtcNow));

        Assert.False(d.Continue);
        Assert.Equal(LoopStopReason.RepeatedFailures, d.StopReason);
    }

    [Fact]
    public void Decide_ModelDone_StopsBeforeMaxIterations()
    {
        var d = LoopPolicy.Decide(
            LoopMode.Autonomous, null, 1, 50, 0, true,
            new WakeupRequest(TimeSpan.FromMinutes(1), "끝", Done: true, DateTimeOffset.UtcNow));

        Assert.False(d.Continue);
        Assert.Equal(LoopStopReason.ModelRequested, d.StopReason);
    }

    [Fact]
    public void Decide_NonPositiveWakeupDelay_IsModelRequestedStop()
    {
        var d = LoopPolicy.Decide(
            LoopMode.Autonomous, null, 1, 50, 0, true,
            new WakeupRequest(TimeSpan.Zero, null, false, DateTimeOffset.UtcNow));

        Assert.False(d.Continue);
        Assert.Equal(LoopStopReason.ModelRequested, d.StopReason);
    }

    [Fact]
    public void Decide_MaxIterationsReached_Stops()
    {
        var d = LoopPolicy.Decide(LoopMode.FixedInterval, FiveMinutes, 3, 3, 0, true, null);

        Assert.False(d.Continue);
        Assert.Equal(LoopStopReason.MaxIterationsReached, d.StopReason);
    }

    [Fact]
    public void Decide_FixedInterval_IgnoresWakeup()
    {
        // A4 — 사용자가 명시한 간격이 모델 판단보다 우선한다.
        var d = LoopPolicy.Decide(
            LoopMode.FixedInterval, FiveMinutes, 1, 50, 0, true,
            new WakeupRequest(TimeSpan.FromSeconds(30), "빨리", Done: true, DateTimeOffset.UtcNow));

        Assert.True(d.Continue);
        Assert.Equal(FiveMinutes, d.Delay);
        Assert.Equal(LoopStopReason.None, d.StopReason);
    }

    [Fact]
    public void Decide_Autonomous_WithoutWakeup_UsesDefaultAndNotifies()
    {
        var d = LoopPolicy.Decide(LoopMode.Autonomous, null, 1, 50, 0, true, null);

        Assert.True(d.Continue);
        Assert.Equal(LoopPolicy.DefaultAutonomousDelay, d.Delay);
        Assert.False(string.IsNullOrWhiteSpace(d.Notice));
    }

    [Fact]
    public void Decide_Autonomous_ClampsWakeupDelay()
    {
        var d = LoopPolicy.Decide(
            LoopMode.Autonomous, null, 1, 50, 0, true,
            new WakeupRequest(TimeSpan.FromSeconds(2), null, false, DateTimeOffset.UtcNow));

        Assert.True(d.Continue);
        Assert.Equal(LoopPolicy.MinAutonomousDelay, d.Delay);
    }

    [Fact]
    public void Decide_FixedInterval_ClampsTooShortInterval()
    {
        var d = LoopPolicy.Decide(LoopMode.FixedInterval, TimeSpan.FromSeconds(1), 1, 50, 0, true, null);

        Assert.True(d.Continue);
        Assert.Equal(LoopPolicy.MinInterval, d.Delay);
    }
}

/// <summary>
/// 상태 표기. GUI 상단바와 헤드리스 stderr 가 같은 문구를 쓰므로 여기서 갈리면 두 UI 가 다른 말을 한다.
/// </summary>
public class LoopStatusFormatterTests
{
    private static LoopStatusSnapshot Snapshot(LoopState state, TimeSpan? remaining = null) => new(
        state, LoopMode.FixedInterval, TimeSpan.FromMinutes(5), "빌드 확인",
        Iteration: 3, MaxIterations: 50, NextRunAt: null, Remaining: remaining,
        ConsecutiveFailures: 0, StopReason: LoopStopReason.None);

    [Fact]
    public void Describe_Idle_IsEmpty()
        => Assert.Equal("", LoopStatusFormatter.Describe(LoopStatusSnapshot.Idle));

    [Fact]
    public void Describe_Waiting_ShowsCountdown()
        => Assert.Equal(
            "루프 실행 중 · 반복 3/50 · 다음 실행까지 2분 14초",
            LoopStatusFormatter.Describe(Snapshot(LoopState.Waiting, new TimeSpan(0, 2, 14))));

    [Fact]
    public void Describe_RunningTurn_ShowsRunning()
        => Assert.Equal(
            "루프 실행 중 · 반복 3/50 · 실행 중",
            LoopStatusFormatter.Describe(Snapshot(LoopState.RunningTurn)));

    [Fact]
    public void DescribeDetailed_Idle_GuidesUser()
        => Assert.Contains("/loop", LoopStatusFormatter.DescribeDetailed(LoopStatusSnapshot.Idle));

    [Fact]
    public void DescribeDetailed_Running_IncludesModeAndPrompt()
    {
        var text = LoopStatusFormatter.DescribeDetailed(Snapshot(LoopState.Waiting, TimeSpan.FromMinutes(1)));
        Assert.Contains("빌드 확인", text);
        Assert.Contains("고정 간격", text);
    }

    [Fact]
    public void StopReasonText_DistinguishesAutoStops()
    {
        // 자동 중지(상한/연속 실패)를 사용자 중지와 같은 문구로 보여주면 안전장치가 작동한 사실을 놓친다.
        Assert.NotEqual(
            LoopStatusFormatter.StopReasonText(LoopStopReason.UserStopped),
            LoopStatusFormatter.StopReasonText(LoopStopReason.MaxIterationsReached));
        Assert.NotEqual(
            LoopStatusFormatter.StopReasonText(LoopStopReason.UserStopped),
            LoopStatusFormatter.StopReasonText(LoopStopReason.RepeatedFailures));
    }
}
