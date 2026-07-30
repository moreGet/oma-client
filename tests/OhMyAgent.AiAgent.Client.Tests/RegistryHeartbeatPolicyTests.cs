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

    /// <summary>
    /// 401/403 은 404 와 달리 재등록으로 치유되지 않는다 — 같은 토큰으로 재시도하면 영원히 실패하므로
    /// 종료(Fatal)가 정답이다. Backoff 로 분류하면 "살아있지만 아무 것도 못 하는" 좀비가 된다.
    /// </summary>
    [Fact]
    public void Unauthorized_is_fatal()
        => Assert.Equal(HeartbeatAction.Fatal, RegistryHeartbeatPolicy.Decide(HeartbeatOutcome.Unauthorized));
}
