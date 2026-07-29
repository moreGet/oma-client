using System;

namespace OhMyAgent.AiAgent.Client.Models.Loop;

/// <summary>루프의 페이싱 방식.</summary>
public enum LoopMode
{
    /// <summary>사용자가 지정한 간격마다 새 턴(`/loop 5m ...`).</summary>
    FixedInterval,
    /// <summary>매 턴 끝에 모델이 schedule_wakeup 으로 다음 시점을 정한다(`/loop ...`).</summary>
    Autonomous,
}

/// <summary>루프 수명의 관측 가능한 단계.</summary>
public enum LoopState
{
    Idle,
    RunningTurn,
    Waiting,
    Stopping,
    Stopped,
}

/// <summary>
/// 루프가 멈춘 이유. "왜 멈췄는지"를 사용자에게 그대로 보여줘야 하기 때문에 열거형으로 고정한다 —
/// 자동 중지(상한/연속 실패)를 사용자 중지와 구분하지 못하면 폭주 방지 장치가 조용히 작동한 사실을 놓친다.
/// </summary>
public enum LoopStopReason
{
    None,
    UserStopped,
    MaxIterationsReached,
    RepeatedFailures,
    ModelRequested,
    Disposed,
    HostShutdown,
}

/// <summary>루프 시작 요청. <paramref name="Interval"/> 은 FixedInterval 모드에서만 의미를 갖는다.</summary>
public sealed record LoopStartRequest(LoopMode Mode, TimeSpan? Interval, string Prompt, int MaxIterations);

/// <summary>schedule_wakeup 이 남긴 "다음 실행 시점" 요청. <paramref name="Done"/> 이면 루프를 접는다.</summary>
public sealed record WakeupRequest(TimeSpan Delay, string? Reason, bool Done, DateTimeOffset RequestedAt);

/// <summary>턴 러너에게 넘기는 1회분 실행 정보.</summary>
public sealed record LoopTurnContext(int Iteration, int MaxIterations, LoopMode Mode, string Prompt);

/// <summary>
/// 턴 1회의 결과. 예외를 밖으로 던지지 않고 성공 여부로 환원한다 —
/// 한 번의 실패로 루프 전체가 무너지면 "일시적 서버 오류"에도 반복 작업이 죽는다.
/// </summary>
public sealed record LoopTurnOutcome(bool Succeeded, string? ErrorMessage)
{
    public static LoopTurnOutcome Ok() => new(true, null);

    public static LoopTurnOutcome Fail(string message) => new(false, message);
}

/// <summary>UI/stderr 가 읽는 루프 상태 1장. 불변 스냅샷이라 스레드 경계를 그대로 넘길 수 있다.</summary>
public sealed record LoopStatusSnapshot(
    LoopState State,
    LoopMode Mode,
    TimeSpan? Interval,
    string Prompt,
    int Iteration,
    int MaxIterations,
    DateTimeOffset? NextRunAt,
    TimeSpan? Remaining,
    int ConsecutiveFailures,
    LoopStopReason StopReason)
{
    /// <summary>루프가 한 번도 시작되지 않은 초기 상태.</summary>
    public static LoopStatusSnapshot Idle { get; } = new(
        LoopState.Idle, LoopMode.FixedInterval, null, "", 0, 0, null, null, 0, LoopStopReason.None);
}

/// <summary><see cref="OhMyAgent.AiAgent.Client.Services.Loop.LoopPolicy"/> 의 턴 후 판정 결과.</summary>
public sealed record LoopDecision(bool Continue, TimeSpan Delay, LoopStopReason StopReason, string? Notice);
