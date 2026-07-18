using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 도구 호출 병렬 실행. 계약:
///  (1) 전부 ReadOnly 인 배치만 병렬 — 승인 게이트/부작용이 없어야 안전하다.
///  (2) 이력(session.Messages)은 "완료 순서"가 아니라 "모델이 호출한 순서"다.
///      tool_result 는 tool_use 와 짝을 이뤄야 하고, 완료 순서로 쓰면 실행마다 이력이 달라져
///      프롬프트 캐시도 무의미해진다.
///  (3) 하나라도 쓰기/실행이 섞이면 배치 전체가 순차.
/// </summary>
public class ParallelToolCallTests
{
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;

    private static AgentOrchestrator Build(IAgentApiClient api, params ITool[] tools)
    {
        var settings = new FakeSettingsService();
        settings.Current.PermissionMode = PermissionMode.FullAuto;
        settings.Current.MaxIterations = 5;

        var ws = new WorkspaceContext(settings);
        return new AgentOrchestrator(
            api, new ToolRegistry(tools), new PermissionService(settings), ws, settings,
            new AllowAllPolicy(), new ContextCompactor(api, settings));
    }

    /// <summary>1턴: 주어진 도구들을 호출 → 2턴: 종료.</summary>
    private static ScriptedApi ApiCalling(params string[] toolNames)
        => new(
            [.. toolNames.Select((n, i) => (AgentStreamEvent)new ToolCallEvent($"call-{i}", n, NoArgs)),
             new MessageStop("tool_use", new Usage(0, 0, 0))],
            [new ContentDelta("완료"), new MessageStop("end_turn", new Usage(0, 0, 0))]);

    private static async Task<List<AgentEvent>> RunAsync(AgentOrchestrator orch, AgentSession session, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in orch.RunAsync("목표", session, ct: ct))
            events.Add(e);
        return events;
    }

    // ── 병렬 동작 ──

    [Fact]
    public async Task AllReadOnlyBatch_RunsConcurrently()
    {
        var tracker = new ConcurrencyTracker();
        var tools = Enumerable.Range(0, 4)
            .Select(i => (ITool)new DelayTool($"read{i}", ToolRisk.ReadOnly, TimeSpan.FromMilliseconds(200), tracker))
            .ToArray();

        var sw = Stopwatch.StartNew();
        await RunAsync(Build(ApiCalling("read0", "read1", "read2", "read3"), tools), new AgentSession());
        sw.Stop();

        Assert.Equal(4, tracker.MaxConcurrent);
        // 순차라면 800ms 이상. 병렬이면 200ms 남짓.
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(600), $"병렬이 아닌 듯 — {sw.ElapsedMilliseconds}ms 소요");
    }

    [Fact]
    public async Task MixedBatch_StaysSequential()
    {
        var tracker = new ConcurrencyTracker();
        ITool[] tools =
        [
            new DelayTool("read0", ToolRisk.ReadOnly, TimeSpan.FromMilliseconds(50), tracker),
            new DelayTool("write0", ToolRisk.Write, TimeSpan.FromMilliseconds(50), tracker),
        ];

        await RunAsync(Build(ApiCalling("read0", "write0"), tools), new AgentSession());

        // 쓰기가 하나라도 섞이면 승인 게이트·부작용 때문에 전부 순차여야 한다.
        Assert.Equal(1, tracker.MaxConcurrent);
    }

    [Fact]
    public async Task SingleCall_StaysSequential()
    {
        var tracker = new ConcurrencyTracker();
        var tool = new DelayTool("read0", ToolRisk.ReadOnly, TimeSpan.Zero, tracker);

        await RunAsync(Build(ApiCalling("read0"), tool), new AgentSession());

        Assert.Equal(1, tracker.MaxConcurrent);
    }

    [Fact]
    public async Task ParallelBatch_RespectsConcurrencyCap()
    {
        var tracker = new ConcurrencyTracker();
        var tools = Enumerable.Range(0, 12)
            .Select(i => (ITool)new DelayTool($"read{i}", ToolRisk.ReadOnly, TimeSpan.FromMilliseconds(80), tracker))
            .ToArray();

        await RunAsync(Build(ApiCalling([.. tools.Select(t => t.Name)]), tools), new AgentSession());

        // 상한이 없으면 12개가 한꺼번에 뜬다. task(서브에이전트) 배치는 하나하나가 LLM 루프라 위험하다.
        Assert.True(tracker.MaxConcurrent <= 8, $"동시 실행 상한 초과: {tracker.MaxConcurrent}");
        Assert.True(tracker.MaxConcurrent > 1, "병렬이 아예 안 됐다");
    }

    // ── 순서 계약 (핵심) ──

    [Fact]
    public async Task History_FollowsCallOrder_NotCompletionOrder()
    {
        var tracker = new ConcurrencyTracker();
        // 첫 번째가 가장 느리다 → 완료 순서는 slow 가 마지막.
        ITool[] tools =
        [
            new DelayTool("slow", ToolRisk.ReadOnly, TimeSpan.FromMilliseconds(250), tracker, result: "SLOW"),
            new DelayTool("fast", ToolRisk.ReadOnly, TimeSpan.Zero, tracker, result: "FAST"),
        ];

        var session = new AgentSession();
        await RunAsync(Build(ApiCalling("slow", "fast"), tools), session);

        var toolResults = session.Messages.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal(2, toolResults.Count);

        // 완료는 fast → slow 였지만 이력은 호출 순서(slow → fast)여야 한다.
        Assert.Equal("SLOW", toolResults[0].Content);
        Assert.Equal("FAST", toolResults[1].Content);
        Assert.Equal("call-0", toolResults[0].ToolCallId);
        Assert.Equal("call-1", toolResults[1].ToolCallId);
    }

    [Fact]
    public async Task ToolResultIds_PairWithAssistantToolCalls()
    {
        var tracker = new ConcurrencyTracker();
        var tools = Enumerable.Range(0, 3)
            .Select(i => (ITool)new DelayTool($"read{i}", ToolRisk.ReadOnly, TimeSpan.FromMilliseconds(i * 30), tracker))
            .ToArray();

        var session = new AgentSession();
        await RunAsync(Build(ApiCalling("read0", "read1", "read2"), tools), session);

        var emitted = session.Messages
            .Where(m => m.ToolCalls is not null)
            .SelectMany(m => m.ToolCalls!)
            .Select(c => c.Id)
            .ToList();
        var referenced = session.Messages.Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolCallId!).ToList();

        // 짝이 깨지면 프로바이더가 요청을 통째로 거부한다.
        Assert.Equal(emitted, referenced);
    }

    [Fact]
    public async Task AllStartEventsFireBeforeResults()
    {
        var tracker = new ConcurrencyTracker();
        var tools = Enumerable.Range(0, 3)
            .Select(i => (ITool)new DelayTool($"read{i}", ToolRisk.ReadOnly, TimeSpan.FromMilliseconds(30), tracker))
            .ToArray();

        var events = await RunAsync(Build(ApiCalling("read0", "read1", "read2"), tools), new AgentSession());

        var lastStart = events.FindLastIndex(e => e is AgentToolCallStarted);
        var firstResult = events.FindIndex(e => e is AgentToolCallResult);

        // UI 가 "N개가 동시에 도는 중"을 보여줄 수 있어야 한다.
        Assert.True(lastStart < firstResult, "시작 이벤트가 결과보다 늦게 나왔다");
        Assert.Equal(3, events.Count(e => e is AgentToolCallStarted));
        Assert.Equal(3, events.Count(e => e is AgentToolCallResult));
    }

    // ── 실패·취소 ──

    [Fact]
    public async Task ParallelBatch_ToolFailureDoesNotKillLoop()
    {
        var tracker = new ConcurrencyTracker();
        ITool[] tools =
        [
            new DelayTool("ok", ToolRisk.ReadOnly, TimeSpan.Zero, tracker, result: "OK"),
            new ThrowingTool("boom"),
        ];

        var session = new AgentSession();
        var events = await RunAsync(Build(ApiCalling("ok", "boom"), tools), session);

        // 도구 예외는 모델에게 오류 결과로 전달돼야지 루프를 죽이면 안 된다.
        Assert.Contains(events, e => e is AgentDone);
        var results = session.Messages.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal(2, results.Count);
        Assert.True(results[1].IsError);
    }

    [Fact]
    public async Task ParallelBatch_CancellationIsReported()
    {
        var tracker = new ConcurrencyTracker();
        using var cts = new CancellationTokenSource();
        var tools = Enumerable.Range(0, 3)
            .Select(i => (ITool)new DelayTool($"read{i}", ToolRisk.ReadOnly, TimeSpan.FromSeconds(5), tracker))
            .ToArray();

        var orch = Build(ApiCalling("read0", "read1", "read2"), tools);
        var session = new AgentSession();

        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var events = await RunAsync(orch, session, cts.Token);

        Assert.Contains(events, e => e is AgentError { Code: "cancelled" });
    }

    // ── 세션 무결성: 취소 후에도 이력이 유효해야 한다(가장 심각했던 버그) ──
    //
    // assistant(tool_use)는 실행 전에 이력에 들어가는데, 취소로 tool_result 를 안 채우면
    // 세션 저장·복원 후 모든 요청이 400 이 된다(tool_use 에 짝 없음).

    [Fact]
    public async Task ParallelBatch_CancellationLeavesEveryToolUsePaired()
    {
        using var cts = new CancellationTokenSource();
        var tracker = new ConcurrencyTracker();
        // 도구 실행이 "시작되는 순간" 취소한다 — assistant(tool_use)는 이미 이력에 있고 실행은 진행 중.
        // 이렇게 해야 스트리밍 단계 취소(오염 자체가 없는 경우)와 레이스하지 않고 결정적으로 검증된다.
        var tools = Enumerable.Range(0, 3)
            .Select(i => (ITool)new DelayTool($"read{i}", ToolRisk.ReadOnly, TimeSpan.FromSeconds(5), tracker,
                onEnter: cts.Cancel))
            .ToArray();

        var session = new AgentSession();
        await RunAsync(Build(ApiCalling("read0", "read1", "read2"), tools), session, cts.Token);

        AssertEveryToolUsePaired(session);
    }

    [Fact]
    public async Task SequentialBatch_CancellationLeavesEveryToolUsePaired()
    {
        using var cts = new CancellationTokenSource();
        var tracker = new ConcurrencyTracker();
        // 쓰기 도구가 섞여 순차 경로를 타게 한다. 첫 도구 실행 시작 시 취소.
        ITool[] tools =
        [
            new DelayTool("write0", ToolRisk.Write, TimeSpan.FromSeconds(5), tracker, onEnter: cts.Cancel),
            new DelayTool("write1", ToolRisk.Write, TimeSpan.FromSeconds(5), tracker),
        ];

        var session = new AgentSession();
        await RunAsync(Build(ApiCalling("write0", "write1"), tools), session, cts.Token);

        AssertEveryToolUsePaired(session);
    }

    /// <summary>이력의 모든 tool_use 에 대응하는 tool_result 가 있는지 — Anthropic 이 요구하는 불변식(오염 방지).</summary>
    private static void AssertEveryToolUsePaired(AgentSession session)
    {
        var toolUseIds = session.Messages
            .Where(m => m.ToolCalls is not null).SelectMany(m => m.ToolCalls!).Select(c => c.Id).ToList();
        var resultIds = session.Messages
            .Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolCallId!).ToHashSet();

        // 실행 시작 시 취소했으므로 tool_use 는 반드시 기록돼 있어야 하고, 전부 짝이 맞아야 한다.
        Assert.NotEmpty(toolUseIds);
        foreach (var id in toolUseIds)
            Assert.Contains(id, resultIds);
    }
}

// ── 테스트용 도구/스텁 ──

/// <summary>동시 실행 수를 관찰한다.</summary>
internal sealed class ConcurrencyTracker
{
    private int _current;
    private int _max;

    public int MaxConcurrent => Volatile.Read(ref _max);

    public void Enter()
    {
        var now = Interlocked.Increment(ref _current);
        int observed;
        while (now > (observed = Volatile.Read(ref _max)))
            Interlocked.CompareExchange(ref _max, now, observed);
    }

    public void Exit() => Interlocked.Decrement(ref _current);
}

internal sealed class DelayTool(
    string name, ToolRisk risk, TimeSpan delay, ConcurrencyTracker tracker,
    string? result = null, Action? onEnter = null) : ITool
{
    public string Name => name;
    public string Description => "테스트 도구";
    public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
    public ToolRisk Risk => risk;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        tracker.Enter();
        try
        {
            onEnter?.Invoke();   // 실행 시작 시점 훅(취소 트리거 등)
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            return ToolResult.Ok(result ?? name);
        }
        finally
        {
            tracker.Exit();
        }
    }
}

internal sealed class ThrowingTool(string name) : ITool
{
    public string Name => name;
    public string Description => "항상 던지는 테스트 도구";
    public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
        => throw new InvalidOperationException("의도적 실패");
}

/// <summary>턴마다 정해진 스트림 이벤트를 방출하는 API 스텁.</summary>
internal sealed class ScriptedApi(params AgentStreamEvent[][] turns) : StubAgentApi
{
    private readonly Queue<AgentStreamEvent[]> _turns = new(turns);

    public override async IAsyncEnumerable<AgentStreamEvent> SendAsync(
        AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var batch = _turns.Count > 0
            ? _turns.Dequeue()
            : [new ContentDelta("완료"), new MessageStop("end_turn", new Usage(0, 0, 0))];

        foreach (var e in batch)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
        await Task.CompletedTask;
    }
}
