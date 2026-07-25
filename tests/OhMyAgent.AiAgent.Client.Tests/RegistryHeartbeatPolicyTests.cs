using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>heartbeat 순수 판정 3케이스(시계 없이). ResolveShell 순수화 패턴.</summary>
public class RegistryHeartbeatPolicyTests
{
    [Fact]
    public void Ok_continues()
        => Assert.Equal(HeartbeatAction.Continue, RegistryHeartbeatPolicy.Decide(HeartbeatOutcome.Ok));

    [Fact]
    public void LeaseExpired_reregisters()
        => Assert.Equal(HeartbeatAction.Reregister, RegistryHeartbeatPolicy.Decide(HeartbeatOutcome.LeaseExpired));

    [Fact]
    public void TransientFailure_backs_off()
        => Assert.Equal(HeartbeatAction.Backoff, RegistryHeartbeatPolicy.Decide(HeartbeatOutcome.TransientFailure));
}
