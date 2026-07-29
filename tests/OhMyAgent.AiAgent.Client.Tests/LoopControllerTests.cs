using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services.Loop;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 루프 엔진의 수명 계약. 이 클래스가 잠그는 것은 하나다 — <b>루프는 반드시 멈춘다</b>.
/// 상한, 연속 실패, 사용자 중지, 외부 취소, Dispose 중 어느 경로로 들어와도 러너 호출이 끊기고
/// 수신 창(IWakeupSink)이 닫혀야 한다.
/// </summary>
public class LoopControllerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static LoopStartRequest Fixed(string prompt = "확인", int max = 3, int seconds = 10)
        => new(LoopMode.FixedInterval, TimeSpan.FromSeconds(seconds), prompt, max);

    private static LoopStartRequest Auto(string prompt = "확인", int max = 3)
        => new(LoopMode.Autonomous, null, prompt, max);

    /// <summary>발화된 이벤트를 모으고 LoopStopped 를 기다릴 수 있게 하는 관찰자.</summary>
    private sealed class Recorder
    {
        private readonly List<LoopEvent> _events = [];
        private readonly TaskCompletionSource<LoopStopped> _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<LoopWaiting> _firstWait =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Recorder(ILoopController loop) => loop.LoopChanged += (_, ev) =>
        {
            lock (_events) _events.Add(ev);
            if (ev is LoopStopped s) _stopped.TrySetResult(s);
            if (ev is LoopWaiting w) _firstWait.TrySetResult(w);
        };

        public IReadOnlyList<LoopEvent> Events { get { lock (_events) return _events.ToArray(); } }

        public Task<LoopStopped> Stopped => _stopped.Task;
        public Task<LoopWaiting> FirstWait => _firstWait.Task;
    }

    /// <summary>Disarm 호출 여부를 세는 스파이 — 수신 창이 닫히지 않으면 루프 밖 예약이 통과한다.</summary>
    private sealed class SpySink : IWakeupSink
    {
        private readonly WakeupSink _inner = new();

        public int ArmCount { get; private set; }
        public int DisarmCount { get; private set; }

        public bool IsArmed => _inner.IsArmed;
        public LoopMode ArmedMode => _inner.ArmedMode;

        public void Arm(Guid loopId, LoopMode mode) { ArmCount++; _inner.Arm(loopId, mode); }
        public void Disarm() { DisarmCount++; _inner.Disarm(); }
        public bool TryWrite(WakeupRequest request, out string? rejectReason) => _inner.TryWrite(request, out rejectReason);
        public bool TryConsume([MaybeNullWhen(false)] out WakeupRequest request) => _inner.TryConsume(out request);
    }

    // ── 정상 수명 ──

    [Fact]
    public async Task FixedInterval_StopsAtMaxIterations()
    {
        var clock = new FakeLoopClock();
        using var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);
        var calls = 0;

        Assert.True(loop.TryStart(Fixed(max: 3), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(LoopTurnOutcome.Ok());
        }, CancellationToken.None, out var error));
        Assert.Null(error);

        var stopped = await rec.Stopped.WaitAsync(Patience);

        Assert.Equal(LoopStopReason.MaxIterationsReached, stopped.Reason);
        Assert.Equal(3, stopped.Iterations);
        Assert.Equal(3, calls);
        Assert.False(loop.IsRunning);
    }

    [Fact]
    public async Task FixedInterval_WaitsClampedInterval()
    {
        var clock = new FakeLoopClock();
        using var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);

        // 1초를 요청해도 하한 10초로 늘어나야 한다 — 여기가 뚫리면 초당 요청이 나간다.
        loop.TryStart(new LoopStartRequest(LoopMode.FixedInterval, TimeSpan.FromSeconds(1), "확인", 2),
            (_, _) => Task.FromResult(LoopTurnOutcome.Ok()), CancellationToken.None, out _);

        var waiting = await rec.FirstWait.WaitAsync(Patience);
        await rec.Stopped.WaitAsync(Patience);

        Assert.Equal(LoopPolicy.MinInterval, waiting.Delay);
    }

    // ── 시작 거부 ──

    [Fact]
    public async Task TryStart_WhileRunning_IsRejected()
    {
        var clock = new FakeLoopClock();
        using var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(loop.TryStart(Fixed(max: 1), async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return LoopTurnOutcome.Ok();
        }, CancellationToken.None, out _));

        await entered.Task.WaitAsync(Patience);

        // A1 — 동시 루프 2개는 같은 세션에 턴을 겹쳐 넣어 대화 이력을 오염시킨다.
        Assert.False(loop.TryStart(Fixed(), (_, _) => Task.FromResult(LoopTurnOutcome.Ok()),
            CancellationToken.None, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        release.TrySetResult();
        await rec.Stopped.WaitAsync(Patience);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryStart_WithoutPrompt_IsRejected(string prompt)
    {
        using var loop = new LoopController(new WakeupSink(), new FakeLoopClock());

        Assert.False(loop.TryStart(Fixed(prompt), (_, _) => Task.FromResult(LoopTurnOutcome.Ok()),
            CancellationToken.None, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(loop.IsRunning);
    }

    // ── 중지 경로 ──

    [Fact]
    public async Task Stop_ReleasesPendingDelay_AndEmitsStoppedOnce()
    {
        var clock = new FakeLoopClock { BlockUntilCanceled = true };
        using var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);

        loop.TryStart(Fixed(max: 50), (_, _) => Task.FromResult(LoopTurnOutcome.Ok()),
            CancellationToken.None, out _);

        await rec.FirstWait.WaitAsync(Patience);   // 대기 진입을 확인한 뒤 중지
        loop.Stop(LoopStopReason.UserStopped);
        loop.Stop(LoopStopReason.UserStopped);     // 멱등 — 두 번 눌러도 이벤트는 한 번
        await loop.StopAndWaitAsync(LoopStopReason.UserStopped);

        var stopped = await rec.Stopped.WaitAsync(Patience);
        Assert.Equal(LoopStopReason.UserStopped, stopped.Reason);
        Assert.Single(rec.Events.OfType<LoopStopped>());
        Assert.False(loop.IsRunning);
    }

    [Fact]
    public async Task ExternalCancellation_StopsLoopAndDisarmsSink()
    {
        var sink = new SpySink();
        var clock = new FakeLoopClock { BlockUntilCanceled = true };
        using var loop = new LoopController(sink, clock);
        var rec = new Recorder(loop);
        using var external = new CancellationTokenSource();

        loop.TryStart(Fixed(max: 50), (_, _) => Task.FromResult(LoopTurnOutcome.Ok()),
            external.Token, out _);

        await rec.FirstWait.WaitAsync(Patience);
        external.Cancel();

        var stopped = await rec.Stopped.WaitAsync(Patience);
        Assert.Equal(LoopStopReason.HostShutdown, stopped.Reason);
        Assert.False(loop.IsRunning);
        Assert.True(sink.DisarmCount >= 1);
        Assert.False(sink.IsArmed);
    }

    [Fact]
    public async Task Dispose_StopsLoop_AndNoFurtherTurns()
    {
        var clock = new FakeLoopClock { BlockUntilCanceled = true };
        var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);
        var calls = 0;

        loop.TryStart(Fixed(max: 50), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(LoopTurnOutcome.Ok());
        }, CancellationToken.None, out _);

        await rec.FirstWait.WaitAsync(Patience);
        loop.Dispose();

        await rec.Stopped.WaitAsync(Patience);
        var afterDispose = Volatile.Read(ref calls);
        await Task.Delay(50);

        Assert.False(loop.IsRunning);
        Assert.Equal(1, afterDispose);
        Assert.Equal(afterDispose, Volatile.Read(ref calls));
    }

    // ── 자율 페이싱 ──

    [Fact]
    public async Task Autonomous_UsesWakeupWrittenDuringTurn()
    {
        var sink = new WakeupSink();
        var clock = new FakeLoopClock();
        using var loop = new LoopController(sink, clock);
        var rec = new Recorder(loop);

        loop.TryStart(Auto(max: 2), (_, _) =>
        {
            sink.TryWrite(new WakeupRequest(TimeSpan.FromSeconds(30), "다음 배포 확인", false, DateTimeOffset.UtcNow), out _);
            return Task.FromResult(LoopTurnOutcome.Ok());
        }, CancellationToken.None, out _);

        var waiting = await rec.FirstWait.WaitAsync(Patience);
        await rec.Stopped.WaitAsync(Patience);

        Assert.Equal(TimeSpan.FromSeconds(30), waiting.Delay);
        Assert.Equal("다음 배포 확인", waiting.Reason);
    }

    [Fact]
    public async Task Autonomous_WithoutWakeup_FallsBackToDefaultAndNotices()
    {
        var clock = new FakeLoopClock();
        using var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);

        loop.TryStart(Auto(max: 2), (_, _) => Task.FromResult(LoopTurnOutcome.Ok()),
            CancellationToken.None, out _);

        var waiting = await rec.FirstWait.WaitAsync(Patience);
        await rec.Stopped.WaitAsync(Patience);

        Assert.Equal(LoopPolicy.DefaultAutonomousDelay, waiting.Delay);
        Assert.NotEmpty(rec.Events.OfType<LoopNotice>());
    }

    [Fact]
    public async Task FixedInterval_DiscardsWakeup()
    {
        // A4 — 고정 간격에서는 모델이 예약을 남겨도 간격이 이긴다.
        var sink = new WakeupSink();
        var clock = new FakeLoopClock();
        using var loop = new LoopController(sink, clock);
        var rec = new Recorder(loop);

        loop.TryStart(Fixed(max: 2, seconds: 60), (_, _) =>
        {
            sink.TryWrite(new WakeupRequest(TimeSpan.FromSeconds(30), "빨리", Done: true, DateTimeOffset.UtcNow), out _);
            return Task.FromResult(LoopTurnOutcome.Ok());
        }, CancellationToken.None, out _);

        var waiting = await rec.FirstWait.WaitAsync(Patience);
        var stopped = await rec.Stopped.WaitAsync(Patience);

        Assert.Equal(TimeSpan.FromSeconds(60), waiting.Delay);
        Assert.Equal(LoopStopReason.MaxIterationsReached, stopped.Reason);
    }

    [Fact]
    public async Task Sink_IsArmedOnlyDuringLoop()
    {
        var sink = new WakeupSink();
        var clock = new FakeLoopClock();
        using var loop = new LoopController(sink, clock);
        var rec = new Recorder(loop);
        var armedDuringTurn = false;

        Assert.False(sink.IsArmed);
        loop.TryStart(Auto(max: 1), (_, _) =>
        {
            armedDuringTurn = sink.IsArmed;
            return Task.FromResult(LoopTurnOutcome.Ok());
        }, CancellationToken.None, out _);

        await rec.Stopped.WaitAsync(Patience);

        Assert.True(armedDuringTurn);
        Assert.False(sink.IsArmed);   // 루프 밖 schedule_wakeup 은 이제 거부된다
    }

    // ── 실패 처리 ──

    [Fact]
    public async Task ThreeConsecutiveFailures_StopLoop()
    {
        var clock = new FakeLoopClock();
        using var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);
        var calls = 0;

        loop.TryStart(Fixed(max: 50), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(LoopTurnOutcome.Fail("서버 오류"));
        }, CancellationToken.None, out _);

        var stopped = await rec.Stopped.WaitAsync(Patience);

        Assert.Equal(LoopStopReason.RepeatedFailures, stopped.Reason);
        Assert.Equal(LoopPolicy.MaxConsecutiveFailures, calls);
    }

    [Fact]
    public async Task SuccessResetsFailureCounter()
    {
        var clock = new FakeLoopClock();
        using var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);
        var calls = 0;

        // 실패-실패-성공-실패-실패 → 연속 3회에 닿지 않으므로 상한(5회)까지 돈다.
        loop.TryStart(Fixed(max: 5), (_, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            return Task.FromResult(n == 3 ? LoopTurnOutcome.Ok() : LoopTurnOutcome.Fail("일시 오류"));
        }, CancellationToken.None, out _);

        var stopped = await rec.Stopped.WaitAsync(Patience);

        Assert.Equal(LoopStopReason.MaxIterationsReached, stopped.Reason);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task RunnerException_IsTreatedAsFailure_NotCrash()
    {
        var clock = new FakeLoopClock();
        using var loop = new LoopController(new WakeupSink(), clock);
        var rec = new Recorder(loop);

        loop.TryStart(Fixed(max: 50), (_, _) => throw new InvalidOperationException("터짐"),
            CancellationToken.None, out _);

        var stopped = await rec.Stopped.WaitAsync(Patience);

        // 러너가 터져도 LoopStopped 는 나와야 한다 — 안 나오면 UI 가 영원히 "실행 중"에 갇힌다.
        Assert.Equal(LoopStopReason.RepeatedFailures, stopped.Reason);
        Assert.All(rec.Events.OfType<LoopTurnFinished>(), f => Assert.False(f.Succeeded));
    }
}
