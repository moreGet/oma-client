using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class A2aHopTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("2", 2)]
    [InlineData("-1", 0)]
    [InlineData("abc", 0)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    public void Read_parses_or_defaults_to_zero(string? header, int expected)
        => Assert.Equal(expected, A2aHop.Read(header));

    [Theory]
    [InlineData(3, 3, true)]   // 경계: hop==limit → 거절
    [InlineData(4, 3, true)]   // 초과 → 거절
    [InlineData(2, 3, false)]  // 미만 → 통과
    [InlineData(0, 3, false)]
    public void ExceedsLimit_rejects_at_or_above_max(int hop, int max, bool expected)
        => Assert.Equal(expected, A2aHop.ExceedsLimit(hop, max));
}
