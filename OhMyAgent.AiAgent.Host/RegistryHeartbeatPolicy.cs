namespace OhMyAgent.AiAgent.Host;

/// <summary>heartbeat 한 주기의 결과 분류.</summary>
public enum HeartbeatOutcome
{
    /// <summary>200 — 정상.</summary>
    Ok,

    /// <summary>404(AgentLeaseExpired) — 소유자 아님/리스 만료 정리.</summary>
    LeaseExpired,

    /// <summary>네트워크/기타 실패 — 일시 오류.</summary>
    TransientFailure,
}

/// <summary>다음 행동.</summary>
public enum HeartbeatAction
{
    /// <summary>다음 주기 계속.</summary>
    Continue,

    /// <summary>재-register 로 자가 치유.</summary>
    Reregister,

    /// <summary>로그 후 다음 주기 재시도(죽지 않음).</summary>
    Backoff,
}

/// <summary>
/// heartbeat 루프의 순수 판정(스펙 §B). I/O·시계 없이 결과→행동만 결정하므로 테스트가 3케이스를 잠근다
/// (ResolveShell 순수화 패턴). 루프는 얇은 I/O 껍데기, 판정은 여기로 격리.
/// </summary>
public static class RegistryHeartbeatPolicy
{
    public static HeartbeatAction Decide(HeartbeatOutcome outcome) => outcome switch
    {
        HeartbeatOutcome.Ok               => HeartbeatAction.Continue,
        HeartbeatOutcome.LeaseExpired     => HeartbeatAction.Reregister,
        HeartbeatOutcome.TransientFailure => HeartbeatAction.Backoff,
        _                                 => HeartbeatAction.Backoff,
    };
}
