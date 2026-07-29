using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Loop;

namespace OhMyAgent.AiAgent.Client.Services.Loop;

/// <summary>
/// ILoopController 기본 구현. 판정(계속/중지/지연)은 전부 <see cref="LoopPolicy"/> 에 위임하고
/// 여기서는 대기·취소·이벤트 발화만 다룬다 — 폭주 방지 규칙이 스레드 코드와 뒤섞이면 검증이 불가능해진다.
/// </summary>
public sealed class LoopController : ILoopController
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly IWakeupSink _wakeups;
    private readonly ILoopClock _clock;
    private readonly object _gate = new();

    private CancellationTokenSource? _innerCts;
    private CancellationTokenSource? _linkedCts;
    private Task? _loopTask;
    private LoopStatusSnapshot _status = LoopStatusSnapshot.Idle;
    private LoopStopReason _requestedStop = LoopStopReason.None;
    private bool _running;
    private bool _disposed;

    public LoopController(IWakeupSink wakeups, ILoopClock? clock = null)
    {
        _wakeups = wakeups ?? throw new ArgumentNullException(nameof(wakeups));
        _clock = clock ?? new SystemLoopClock();
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public LoopStatusSnapshot Status
    {
        get { lock (_gate) return _status; }
    }

    public event EventHandler<LoopEvent>? LoopChanged;

    public bool TryStart(LoopStartRequest request, LoopTurnRunner runner, CancellationToken externalCt, out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runner);

        lock (_gate)
        {
            if (_disposed)
            {
                error = "루프를 시작할 수 없습니다(세션이 이미 정리되었습니다).";
                return false;
            }

            // 동시 루프 봉쇄(A1) — 두 루프가 같은 세션에 턴을 겹쳐 넣으면 대화 이력이 뒤섞인다.
            if (_running)
            {
                error = "이미 루프가 실행 중입니다. /loop stop 으로 중지하세요.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                error = "반복할 프롬프트가 필요합니다. 예: /loop 5m 빌드 상태 확인";
                return false;
            }

            // 시작 시점에 한 번만 클램프한다 — 이후 판정은 이미 안전한 값만 본다.
            TimeSpan? interval = request.Mode == LoopMode.FixedInterval
                ? LoopPolicy.ClampInterval(request.Interval ?? LoopPolicy.MinInterval)
                : null;
            var normalized = request with
            {
                Interval = interval,
                MaxIterations = LoopPolicy.ClampMaxIterations(request.MaxIterations),
                Prompt = request.Prompt.Trim(),
            };

            _requestedStop = LoopStopReason.None;
            _innerCts?.Dispose();
            _innerCts = new CancellationTokenSource();
            _linkedCts?.Dispose();
            // 외부 토큰(앱 종료/로그아웃)과 내부 중지를 한 토큰으로 합쳐 대기와 턴 실행 양쪽에 같은 취소를 전달한다.
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _innerCts.Token);

            _running = true;
            _status = new LoopStatusSnapshot(
                LoopState.RunningTurn, normalized.Mode, normalized.Interval, normalized.Prompt,
                0, normalized.MaxIterations, null, null, 0, LoopStopReason.None);

            var token = _linkedCts.Token;
            var loopId = Guid.NewGuid();
            // 비차단 시작 — 호출자(슬래시 커맨드 처리)는 즉시 반환되어야 입력창이 잠기지 않는다.
            _loopTask = Task.Run(() => RunLoopAsync(normalized, runner, loopId, token), CancellationToken.None);
        }

        error = null;
        return true;
    }

    public void Stop(LoopStopReason reason)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_running) return;
            // 첫 중지 사유가 이긴다 — 종료 처리 중 들어온 중복 요청이 사유를 덮어쓰면 안내가 뒤바뀐다.
            if (_requestedStop == LoopStopReason.None) _requestedStop = reason;
            _status = _status with { State = LoopState.Stopping };
            cts = _innerCts;
        }

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* 이미 정리된 경로 */ }
    }

    public async Task StopAndWaitAsync(LoopStopReason reason, TimeSpan? timeout = null)
    {
        Stop(reason);

        Task? task;
        lock (_gate) task = _loopTask;
        if (task is null) return;

        try { await task.WaitAsync(timeout ?? DefaultJoinTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // 종료를 무한정 붙잡지 않는다 — 러너가 응답하지 않아도 프로세스는 접혀야 한다.
            AppLog.Warn("Loop", "루프 종료 대기 시간을 초과했습니다.");
        }
        catch (OperationCanceledException) { /* 정상 종료 경로 */ }
    }

    public void Dispose()
    {
        Task? task;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            task = _loopTask;
        }

        Stop(LoopStopReason.Disposed);

        // 여기서 접지 않으면 창이 닫힌 뒤에도 턴이 한 번 더 돌아 서버 호출이 나간다.
        try { task?.Wait(DefaultJoinTimeout); }
        catch (Exception) { /* 종료 경로 — 더 할 일이 없다 */ }

        lock (_gate)
        {
            _linkedCts?.Dispose();
            _linkedCts = null;
            _innerCts?.Dispose();
            _innerCts = null;
        }
    }

    private async Task RunLoopAsync(LoopStartRequest req, LoopTurnRunner runner, Guid loopId, CancellationToken ct)
    {
        var iteration = 0;
        var failures = 0;
        var stop = LoopStopReason.None;

        Emit(new LoopStarted(Status));

        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested) { stop = RequestedStopOr(LoopStopReason.HostShutdown); break; }

                iteration++;
                UpdateStatus(s => s with
                {
                    State = LoopState.RunningTurn,
                    Iteration = iteration,
                    NextRunAt = null,
                    Remaining = null,
                });
                Emit(new LoopTurnStarting(iteration, req.MaxIterations));

                // 수신 창 개방 — 이 턴 동안 schedule_wakeup 이 쓴 값만 회수 대상이 된다.
                _wakeups.Arm(loopId, req.Mode);

                LoopTurnOutcome outcome;
                try
                {
                    outcome = await runner(new LoopTurnContext(iteration, req.MaxIterations, req.Mode, req.Prompt), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    stop = RequestedStopOr(LoopStopReason.HostShutdown);
                    break;
                }
                catch (Exception ex)
                {
                    // 러너가 예외를 흘려도 루프는 정책(연속 실패 상한)으로 접혀야 한다 —
                    // 여기서 터지면 LoopStopped 도 못 내보내고 UI 는 영원히 "실행 중"으로 남는다.
                    outcome = LoopTurnOutcome.Fail(ex.Message);
                }

                var wakeup = _wakeups.TryConsume(out var consumed) ? consumed : null;
                if (req.Mode == LoopMode.FixedInterval) wakeup = null;   // A4 — 사용자가 정한 간격이 모델 판단을 이긴다.

                failures = outcome.Succeeded ? 0 : failures + 1;
                UpdateStatus(s => s with { ConsecutiveFailures = failures });
                Emit(new LoopTurnFinished(iteration, outcome.Succeeded, outcome.ErrorMessage));

                var decision = LoopPolicy.Decide(
                    req.Mode, req.Interval, iteration, req.MaxIterations, failures, outcome.Succeeded, wakeup);

                if (!string.IsNullOrEmpty(decision.Notice)) Emit(new LoopNotice(decision.Notice));
                if (!decision.Continue) { stop = decision.StopReason; break; }
                if (ct.IsCancellationRequested) { stop = RequestedStopOr(LoopStopReason.HostShutdown); break; }

                var nextAt = _clock.UtcNow + decision.Delay;
                UpdateStatus(s => s with
                {
                    State = LoopState.Waiting,
                    NextRunAt = nextAt,
                    Remaining = decision.Delay,
                });
                Emit(new LoopWaiting(nextAt, decision.Delay, wakeup?.Reason));

                if (!await WaitAsync(nextAt, iteration, req.MaxIterations, ct).ConfigureAwait(false))
                {
                    stop = RequestedStopOr(LoopStopReason.HostShutdown);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // 예상 밖 예외로도 상태가 "실행 중"에 갇히면 안 된다 — 사유 없이 접고 로그로 남긴다.
            AppLog.Error("Loop", $"루프가 예외로 중단되었습니다: {ex.Message}", ex);
        }
        finally
        {
            _wakeups.Disarm();   // 수신 창 폐쇄 — 루프 밖 schedule_wakeup 호출은 이제 거부된다.
            lock (_gate)
            {
                _running = false;
                _status = _status with
                {
                    State = LoopState.Stopped,
                    StopReason = stop,
                    NextRunAt = null,
                    Remaining = null,
                };
            }
        }

        Emit(new LoopStopped(stop, iteration));
    }

    /// <summary>다음 실행 시각까지 1초 단위로 쉰다. 취소되면 false.</summary>
    private async Task<bool> WaitAsync(DateTimeOffset nextAt, int iteration, int maxIterations, CancellationToken ct)
    {
        // 최대 24시간짜리 대기를 통으로 걸면 중지 반응이 그만큼 늦고 카운트다운도 그릴 수 없다.
        while (true)
        {
            if (ct.IsCancellationRequested) return false;

            var remaining = nextAt - _clock.UtcNow;
            if (remaining <= TimeSpan.Zero) return true;

            UpdateStatus(s => s with { Remaining = remaining });
            Emit(new LoopTick(remaining, iteration, maxIterations));

            var step = remaining < TickInterval ? remaining : TickInterval;
            try { await _clock.DelayAsync(step, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    /// <summary>명시적 중지 사유가 있으면 그것을, 없으면(= 외부 토큰 취소) fallback 을 쓴다.</summary>
    private LoopStopReason RequestedStopOr(LoopStopReason fallback)
    {
        lock (_gate) return _requestedStop != LoopStopReason.None ? _requestedStop : fallback;
    }

    private void UpdateStatus(Func<LoopStatusSnapshot, LoopStatusSnapshot> mutate)
    {
        lock (_gate) _status = mutate(_status);
    }

    private void Emit(LoopEvent ev)
    {
        var handler = LoopChanged;
        if (handler is null) return;

        // 구독자(UI 마샬 등) 예외가 루프를 죽이면 안 된다 — 표시 실패는 루프 중단 사유가 아니다.
        try { handler(this, ev); }
        catch (Exception ex) { AppLog.Warn("Loop", $"루프 이벤트 구독자 예외: {ex.Message}"); }
    }
}
