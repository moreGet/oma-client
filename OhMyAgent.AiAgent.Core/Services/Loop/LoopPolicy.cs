using System;
using OhMyAgent.AiAgent.Client.Models.Loop;

namespace OhMyAgent.AiAgent.Client.Services.Loop;

/// <summary>
/// 폭주 방지 상수 + 턴 후 판정. 이 타입에는 시계도 타이머도 오케스트레이터도 없다 —
/// "언제 멈추는가" 를 순수 함수 한 곳에 모아 두어야 상한이 실제로 작동하는지 테스트로 못 박을 수 있다.
/// </summary>
public static class LoopPolicy
{
    // 하한이 필요한 이유: 간격 0~수초짜리 루프는 서버 쿼터를 순식간에 태우고 사용자가 개입할 틈도 없다.
    // 상한이 필요한 이유: 24시간을 넘는 대기는 프로세스 수명보다 길어 사실상 "영원히 안 도는 루프"다.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(24);

    // 자율 페이싱은 모델이 값을 정하므로 하한/상한을 더 좁게 잡는다(모델이 0 을 넣어 폭주시키는 것을 막는다).
    public static readonly TimeSpan MinAutonomousDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxAutonomousDelay = TimeSpan.FromHours(1);

    /// <summary>모델이 다음 시점을 정하지 않았을 때의 보수적 기본값.</summary>
    public static readonly TimeSpan DefaultAutonomousDelay = TimeSpan.FromMinutes(5);

    public const int DefaultMaxIterations = 50;

    /// <summary>설정으로도 뚫을 수 없는 하드 상한 — 설정 파일 오타 하나로 무한 루프가 되면 안 된다.</summary>
    public const int HardMaxIterations = 1000;

    /// <summary>연속 실패 상한. 같은 오류로 계속 실패하는 루프는 재시도해도 결과가 같다.</summary>
    public const int MaxConsecutiveFailures = 3;

    public static TimeSpan ClampInterval(TimeSpan value)
        => value < MinInterval ? MinInterval : value > MaxInterval ? MaxInterval : value;

    /// <summary>모델이 넘긴 초 단위 지연을 클램프. NaN/Infinity 는 기본값으로 되돌린다(모델 출력은 신뢰하지 않는다).</summary>
    public static TimeSpan ClampAutonomousDelay(double delaySeconds)
    {
        if (!double.IsFinite(delaySeconds)) return DefaultAutonomousDelay;
        var span = TimeSpan.FromSeconds(delaySeconds);
        return span < MinAutonomousDelay ? MinAutonomousDelay
             : span > MaxAutonomousDelay ? MaxAutonomousDelay
             : span;
    }

    public static TimeSpan ClampAutonomousDelay(TimeSpan value) => ClampAutonomousDelay(value.TotalSeconds);

    public static int ClampMaxIterations(int value)
        => value < 1 ? 1 : value > HardMaxIterations ? HardMaxIterations : value;

    /// <summary>
    /// 턴 1회가 끝난 직후 "계속할지 / 얼마나 쉴지 / 왜 멈추는지" 를 결정하는 유일한 지점.
    /// 판정 순서가 곧 우선순위다(실패 차단 &gt; 모델 종료 요청 &gt; 반복 상한 &gt; 페이싱).
    /// </summary>
    /// <param name="interval">FixedInterval 모드에서만 non-null(이미 클램프된 값).</param>
    /// <param name="completedIterations">방금 끝난 턴을 포함한 누적 실행 횟수.</param>
    /// <param name="lastTurnSucceeded">
    /// 판정에는 쓰지 않는다(연속 실패 카운터가 이미 이 정보를 담고 있다).
    /// 호출부가 카운터와 결과를 함께 넘기도록 강제해, 카운터 갱신을 빠뜨린 배선을 눈에 띄게 하려는 인자다.
    /// </param>
    /// <param name="wakeup">Autonomous 모드에서 이번 턴에 회수한 값(없으면 null).</param>
    public static LoopDecision Decide(
        LoopMode mode,
        TimeSpan? interval,
        int completedIterations,
        int maxIterations,
        int consecutiveFailures,
        bool lastTurnSucceeded,
        WakeupRequest? wakeup)
    {
        _ = lastTurnSucceeded;

        // 1) 연속 실패가 최우선 — 모델이 계속 돌자고 해도 실패가 이긴다.
        if (consecutiveFailures >= MaxConsecutiveFailures)
            return new LoopDecision(false, TimeSpan.Zero, LoopStopReason.RepeatedFailures, null);

        // 2) 모델의 명시적 종료 요청(자율 모드 전용 — 고정 간격에서는 간격이 이긴다).
        if (mode == LoopMode.Autonomous && wakeup is not null && (wakeup.Done || wakeup.Delay <= TimeSpan.Zero))
            return new LoopDecision(false, TimeSpan.Zero, LoopStopReason.ModelRequested, null);

        // 3) 반복 상한.
        if (completedIterations >= maxIterations)
            return new LoopDecision(false, TimeSpan.Zero, LoopStopReason.MaxIterationsReached, null);

        // 4) 고정 간격 — 사용자가 명시한 간격이 모델 판단보다 우선한다.
        if (mode == LoopMode.FixedInterval)
            return new LoopDecision(true, ClampInterval(interval ?? MinInterval), LoopStopReason.None, null);

        // 5) 자율 페이싱 — 모델이 정한 값을 클램프해 사용.
        if (wakeup is not null)
            return new LoopDecision(true, ClampAutonomousDelay(wakeup.Delay), LoopStopReason.None, null);

        // 6) 자율인데 모델이 아무것도 정하지 않음 — 즉시 재실행하면 폭주라 보수적 기본값으로 쉰다.
        return new LoopDecision(true, DefaultAutonomousDelay, LoopStopReason.None,
            "모델이 다음 실행 시점을 정하지 않아 기본 5분을 사용합니다.");
    }
}
