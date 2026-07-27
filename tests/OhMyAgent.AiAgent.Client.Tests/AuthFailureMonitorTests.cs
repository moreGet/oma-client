using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 인증 실패 판정 — 시계·I/O 없이 순수하게 잠근다(RegistryHeartbeatPolicy 와 동일 패턴).
/// 핵심은 두 가지: (1) 임계값 전에는 죽지 않는다(일시적 401 흡수), (2) 확정은 한 번만 신호한다.
/// </summary>
public class AuthFailureMonitorTests
{
    [Fact]
    public void Below_threshold_is_not_fatal()
    {
        var m = new AuthFailureMonitor(threshold: 3);

        Assert.False(m.RecordFailure());
        Assert.False(m.RecordFailure());

        Assert.False(m.IsFatal);
        Assert.Equal(2, m.ConsecutiveFailures);
    }

    [Fact]
    public void Reaching_threshold_confirms_once()
    {
        var m = new AuthFailureMonitor(threshold: 3);

        m.RecordFailure();
        m.RecordFailure();

        // 임계값에 닿은 그 호출만 true — 호출자가 종료 절차를 중복 실행하지 않게.
        Assert.True(m.RecordFailure());
        Assert.True(m.IsFatal);
        Assert.False(m.RecordFailure());
    }

    /// <summary>
    /// 서버 재기동·키 회전 중 401 이 한두 번 스칠 수 있다. 성공이 끼면 연속이 아니므로 초기화한다 —
    /// 이게 없으면 며칠에 걸친 산발적 401 이 누적돼 멀쩡한 데몬을 죽인다.
    /// </summary>
    [Fact]
    public void Success_resets_the_streak()
    {
        var m = new AuthFailureMonitor(threshold: 3);

        m.RecordFailure();
        m.RecordFailure();
        m.RecordSuccess();

        Assert.Equal(0, m.ConsecutiveFailures);

        Assert.False(m.RecordFailure());
        Assert.False(m.RecordFailure());
        Assert.False(m.IsFatal);
    }

    /// <summary>확정 이후의 성공은 판정을 되돌리지 않는다 — 종료는 이미 진행 중이다.</summary>
    [Fact]
    public void Success_after_fatal_does_not_revive()
    {
        var m = new AuthFailureMonitor(threshold: 1);

        Assert.True(m.RecordFailure());
        m.RecordSuccess();

        Assert.True(m.IsFatal);
    }

    [Fact]
    public void Non_positive_threshold_falls_back_to_default()
    {
        var m = new AuthFailureMonitor(threshold: 0);

        for (var i = 1; i < AuthFailureMonitor.DefaultThreshold; i++)
            Assert.False(m.RecordFailure());

        Assert.True(m.RecordFailure());
    }

    [Theory]
    [InlineData("http_401")]
    [InlineData("http_403")]
    [InlineData("unauthorized")]
    [InlineData("UNAUTHORIZED")]
    [InlineData("invalid_token")]
    [InlineData("token_expired")]
    public void Auth_error_codes_are_recognized(string code)
        => Assert.True(AuthFailureMonitor.IsAuthErrorCode(code));

    /// <summary>도구 오류·모델 오류·취소는 인증과 무관하다 — 이걸 인증으로 세면 멀쩡한 데몬이 죽는다.</summary>
    [Theory]
    [InlineData("http_500")]
    [InlineData("http_429")]
    [InlineData("rate_limited")]
    [InlineData("max_iterations")]
    [InlineData("cancelled")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_auth_codes_are_ignored(string? code)
        => Assert.False(AuthFailureMonitor.IsAuthErrorCode(code));
}
