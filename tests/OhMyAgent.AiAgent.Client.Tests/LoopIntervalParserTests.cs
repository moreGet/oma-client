using System;
using OhMyAgent.AiAgent.Client.Services.Loop;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// /loop 의 간격 토큰 파서. 오분류하면 사용자가 쓴 문장이 잘리거나("5 분마다 확인" → 프롬프트 "분마다 확인"),
/// 반대로 간격 지정이 프롬프트로 새어 자율 페이싱으로 돌아 버린다.
/// </summary>
public class LoopIntervalParserTests
{
    [Theory]
    [InlineData("10s", 10)]
    [InlineData("90S", 90)]
    [InlineData("5sec", 5)]
    [InlineData("5secs", 5)]
    [InlineData("5m", 300)]
    [InlineData("5min", 300)]
    [InlineData("5mins", 300)]
    [InlineData("2h", 7200)]
    [InlineData("1.5h", 5400)]
    [InlineData("3hrs", 10800)]
    [InlineData("2hr", 7200)]
    public void TryParse_ValidTokens(string token, double expectedSeconds)
    {
        Assert.True(LoopIntervalParser.TryParse(token, out var span));
        Assert.Equal(expectedSeconds, span.TotalSeconds, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("5")]      // 단위 없는 숫자는 프롬프트의 일부다(A5)
    [InlineData("m")]
    [InlineData("-5m")]
    [InlineData("0m")]     // 0 간격은 곧 폭주다
    [InlineData("0s")]
    [InlineData("5x")]
    [InlineData("5 m")]    // 공백이 끼면 첫 토큰이 아니다
    [InlineData("1e3s")]
    public void TryParse_InvalidTokens(string? token)
    {
        Assert.False(LoopIntervalParser.TryParse(token, out var span));
        Assert.Equal(TimeSpan.Zero, span);
    }

    [Theory]
    [InlineData(0, 0, 8, "8초")]
    [InlineData(0, 2, 14, "2분 14초")]
    [InlineData(0, 5, 0, "5분")]
    [InlineData(1, 5, 0, "1시간 5분")]
    [InlineData(24, 0, 0, "24시간")]
    public void Format_HumanReadable(int h, int m, int s, string expected)
        => Assert.Equal(expected, LoopIntervalParser.Format(new TimeSpan(h, m, s)));

    [Fact]
    public void Format_NonPositive_IsZeroSeconds()
    {
        Assert.Equal("0초", LoopIntervalParser.Format(TimeSpan.Zero));
        Assert.Equal("0초", LoopIntervalParser.Format(TimeSpan.FromSeconds(-5)));
    }
}
