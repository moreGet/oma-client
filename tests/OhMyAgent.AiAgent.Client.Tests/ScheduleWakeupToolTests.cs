using System;
using System.Text.Json;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Loop;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// schedule_wakeup. 이 도구의 존재 이유는 "모델이 다음 실행 시점을 정하되, 정하는 값이 안전 범위를 벗어날 수
/// 없게" 하는 것이다. 루프 밖 호출·클램프 누락·중복 소비가 뚫리면 자율 페이싱이 곧 폭주가 된다.
/// </summary>
public class ScheduleWakeupToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext Ctx()
        => new(new WorkspaceContext(new FakeSettingsService()), PermissionMode.FullAuto);

    private static (ScheduleWakeupTool Tool, WakeupSink Sink) Make(LoopMode? armed = null)
    {
        var sink = new WakeupSink();
        if (armed is { } mode) sink.Arm(Guid.NewGuid(), mode);
        return (new ScheduleWakeupTool(sink), sink);
    }

    [Fact]
    public void Metadata_IsStable()
    {
        var (tool, _) = Make();
        Assert.Equal("schedule_wakeup", tool.Name);
        // ReadOnly 라야 승인 카드 없이 자동 실행된다 — 루프 페이싱은 사용자 확인을 요구할 성질이 아니다.
        Assert.Equal(ToolRisk.ReadOnly, tool.Risk);
    }

    [Fact]
    public async Task NotArmed_Fails_AndWritesNothing()
    {
        var (tool, sink) = Make();

        var result = await tool.ExecuteAsync(Args("""{"delaySeconds":30}"""), Ctx());

        Assert.True(result.IsError);
        Assert.False(sink.TryConsume(out _));
    }

    [Fact]
    public async Task FixedIntervalLoop_Succeeds_ButDiscardsValue()
    {
        // A4 — 실패로 돌려주면 모델이 재시도로 턴을 낭비한다. 성공 + 무시 안내로 끝낸다.
        var (tool, sink) = Make(LoopMode.FixedInterval);

        var result = await tool.ExecuteAsync(Args("""{"delaySeconds":30}"""), Ctx());

        Assert.False(result.IsError);
        Assert.False(sink.TryConsume(out _));
    }

    [Fact]
    public async Task Autonomous_ClampsTooShortDelay_AndSaysSo()
    {
        var (tool, sink) = Make(LoopMode.Autonomous);

        var result = await tool.ExecuteAsync(Args("""{"delaySeconds":2}"""), Ctx());

        Assert.False(result.IsError);
        Assert.Contains("조정", result.Content);
        Assert.True(sink.TryConsume(out var wakeup));
        Assert.Equal(LoopPolicy.MinAutonomousDelay, wakeup.Delay);
    }

    [Fact]
    public async Task Autonomous_KeepsInRangeDelay_WithoutClampNote()
    {
        var (tool, sink) = Make(LoopMode.Autonomous);

        var result = await tool.ExecuteAsync(Args("""{"delaySeconds":300,"reason":"5분 뒤 재확인"}"""), Ctx());

        Assert.False(result.IsError);
        Assert.DoesNotContain("조정", result.Content);
        Assert.True(sink.TryConsume(out var wakeup));
        Assert.Equal(TimeSpan.FromMinutes(5), wakeup.Delay);
        Assert.Equal("5분 뒤 재확인", wakeup.Reason);
    }

    [Fact]
    public async Task Autonomous_ClampsTooLongDelay()
    {
        var (tool, sink) = Make(LoopMode.Autonomous);

        var result = await tool.ExecuteAsync(Args("""{"delaySeconds":99999}"""), Ctx());

        Assert.False(result.IsError);
        Assert.True(sink.TryConsume(out var wakeup));
        Assert.Equal(LoopPolicy.MaxAutonomousDelay, wakeup.Delay);
    }

    [Fact]
    public async Task Done_MarksLoopFinished()
    {
        var (tool, sink) = Make(LoopMode.Autonomous);

        var result = await tool.ExecuteAsync(Args("""{"delaySeconds":60,"done":true}"""), Ctx());

        Assert.False(result.IsError);
        Assert.True(sink.TryConsume(out var wakeup));
        Assert.True(wakeup.Done);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"reason":"곧"}""")]
    [InlineData("""{"delaySeconds":"30"}""")]   // 문자열은 숫자가 아니다 — 조용히 파싱하면 오타가 폭주로 이어진다
    [InlineData("""{"delaySeconds":null}""")]
    public async Task MissingOrNonNumericDelay_Fails(string json)
    {
        var (tool, sink) = Make(LoopMode.Autonomous);

        var result = await tool.ExecuteAsync(Args(json), Ctx());

        Assert.True(result.IsError);
        Assert.False(sink.TryConsume(out _));
    }

    [Fact]
    public async Task TwiceInOneTurn_LastWriteWins_AndConsumeIsSingleShot()
    {
        var (tool, sink) = Make(LoopMode.Autonomous);

        await tool.ExecuteAsync(Args("""{"delaySeconds":60}"""), Ctx());
        await tool.ExecuteAsync(Args("""{"delaySeconds":600}"""), Ctx());

        Assert.True(sink.TryConsume(out var wakeup));
        Assert.Equal(TimeSpan.FromMinutes(10), wakeup.Delay);
        // 두 번째 소비는 실패해야 한다 — 아니면 지난 턴의 결정이 다음 턴에 되살아난다.
        Assert.False(sink.TryConsume(out _));
    }

    [Fact]
    public async Task Disarm_DropsPendingValue()
    {
        var (tool, sink) = Make(LoopMode.Autonomous);
        await tool.ExecuteAsync(Args("""{"delaySeconds":60}"""), Ctx());

        sink.Disarm();

        Assert.False(sink.TryConsume(out _));
        Assert.False(sink.IsArmed);
    }
}
