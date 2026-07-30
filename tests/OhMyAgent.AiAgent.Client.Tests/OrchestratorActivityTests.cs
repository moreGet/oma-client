using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 오케스트레이터 계측. 계약:
///  (1) 실행 중에는 턴·도구가 등기소에 보이고, <b>끝나면 하나도 남지 않는다</b>(정상·취소·예외 전부).
///  (2) 도구 아래에서 시작된 자식 프로세스·하위 턴은 그 도구 밑으로 자동 중첩된다(주변 소유자).
///  (3) 도구 하나만 중지하면 그 도구만 실패로 끝나고 <b>턴은 계속 간다</b> — 이력의 tool_use/tool_result 짝도 유지된다.
///  (4) 턴을 중지하면 종전 사용자 중지와 동일하게 대화가 접힌다.
/// </summary>
public class OrchestratorActivityTests
{
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;
    private static readonly DateTimeOffset T0 = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static AgentOrchestrator Build(IAgentActivityRegistry? activities, IAgentApiClient api, params ITool[] tools)
    {
        var settings = new FakeSettingsService();
        settings.Current.PermissionMode = PermissionMode.FullAuto;
        settings.Current.MaxIterations = 5;

        return new AgentOrchestrator(
            api, new ToolRegistry(tools), new PermissionService(settings), new WorkspaceContext(settings),
            settings, new AllowAllPolicy(), new ContextCompactor(api, settings), activities: activities);
    }

    /// <summary>1턴: 주어진 도구들을 호출 → 2턴: 종료.</summary>
    private static ScriptedApi ApiCalling(params string[] toolNames)
        => new(
            [.. toolNames.Select((n, i) => (AgentStreamEvent)new ToolCallEvent($"call-{i}", n, NoArgs)),
             new MessageStop("tool_use", new Usage(0, 0, 0))],
            [new ContentDelta("완료"), new MessageStop("end_turn", new Usage(0, 0, 0))]);

    private static async Task<List<AgentEvent>> RunAsync(
        AgentOrchestrator orch, AgentSession session, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in orch.RunAsync("목표", session, ct: ct))
            events.Add(e);
        return events;
    }

    // ── 가시성 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Turn_And_Tool_AreVisibleWhileRunning()
    {
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);
        AgentActivityView? seen = null;
        var tool = new ObservingTool("read0", ToolRisk.ReadOnly, registry, v => seen = v);

        await RunAsync(Build(registry, ApiCalling("read0"), tool), new AgentSession());

        Assert.NotNull(seen);
        Assert.Equal(new[] { AgentActivityKind.Turn, AgentActivityKind.Tool }, seen!.Items.Select(i => i.Kind));
        Assert.Equal("read0", seen.Items[1].Title);
        Assert.Equal(ToolRisk.ReadOnly, seen.Items[1].Risk);
        Assert.Equal(1, seen.Items[0].Iteration);   // 첫 반복 진행이 보여야 한다
        Assert.Equal(5, seen.Items[0].MaxIterations);
    }

    [Fact]
    public async Task Registry_IsEmptyAfterCompletion()
    {
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);
        var tool = new ObservingTool("read0", ToolRisk.ReadOnly, registry, _ => { });

        await RunAsync(Build(registry, ApiCalling("read0"), tool), new AgentSession());

        // 누수 검사 — 등록만 되고 해제 안 되는 경로가 있으면 여기서 잡힌다.
        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public async Task Registry_IsEmptyAfterToolThrows()
    {
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);

        await RunAsync(Build(registry, ApiCalling("boom"), new ThrowingTool("boom")), new AgentSession());

        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public async Task Registry_IsEmptyAfterUserCancellation()
    {
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);
        using var cts = new CancellationTokenSource();
        var tracker = new ConcurrencyTracker();
        var tools = Enumerable.Range(0, 3)
            .Select(i => (ITool)new DelayTool($"read{i}", ToolRisk.ReadOnly, TimeSpan.FromSeconds(5), tracker,
                onEnter: cts.Cancel))
            .ToArray();

        var events = await RunAsync(
            Build(registry, ApiCalling("read0", "read1", "read2"), tools), new AgentSession(), cts.Token);

        Assert.Contains(events, e => e is AgentError { Code: "cancelled" });
        // 취소 경로에서도 반복자 정리와 함께 전부 해제돼야 한다(병렬 8개 포함).
        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public async Task QueuedParallelCalls_AreVisibleBeforeTheyStart()
    {
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);
        var observed = new List<int>();
        var gate = new object();

        // 동시 상한은 8인데 호출은 10개다 — 줄 서 있는 2개도 목록에 보여야 사용자가 그것을 중지할 수 있다.
        var tools = Enumerable.Range(0, 10)
            .Select(i => (ITool)new LateObservingTool($"read{i}", registry, count =>
            {
                lock (gate) observed.Add(count);
            }))
            .ToArray();

        await RunAsync(Build(registry, ApiCalling([.. tools.Select(t => t.Name)]), tools), new AgentSession());

        Assert.True(observed.Max() > 8 + 1,
            $"세마포어 대기 중인 호출이 목록에 없다 — 관측된 최대 항목 수 {observed.Max()} (턴 1 + 도구)");
        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public async Task SequentialTools_ReleaseScopeBetweenCalls()
    {
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);
        var seen = new List<int>();

        // 쓰기 도구가 섞여 순차 경로를 탄다. 반복자 루프 안의 using 이 매 회 해제되지 않으면
        // 두 번째 도구가 볼 때 첫 번째가 아직 남아 항목이 3개가 된다.
        ITool[] tools =
        [
            new ObservingTool("write0", ToolRisk.Write, registry, v => seen.Add(v.Items.Count)),
            new ObservingTool("write1", ToolRisk.Write, registry, v => seen.Add(v.Items.Count)),
        ];

        await RunAsync(Build(registry, ApiCalling("write0", "write1"), tools), new AgentSession());

        Assert.Equal(new[] { 2, 2 }, seen);   // 매번 턴 1 + 도구 1
        Assert.Empty(registry.Snapshot().Items);
    }

    // ── 계층: 주변 소유자(AsyncLocal) ────────────────────────────────────────

    [Fact]
    public async Task ChildProcessStartedByTool_NestsUnderThatTool()
    {
        var probe = new FakeProcessProbe();
        probe.Add(4242, "fake", T0);
        var registry = new AgentActivityRegistry(probe, () => T0);

        AgentActivityView? seen = null;
        var tool = new ProcessSpawningTool("spawn", registry, 4242, v => seen = v);

        await RunAsync(Build(registry, ApiCalling("spawn"), tool), new AgentSession());

        Assert.NotNull(seen);
        Assert.Equal(
            new[] { AgentActivityKind.Turn, AgentActivityKind.Tool, AgentActivityKind.ChildProcess },
            seen!.Items.Select(i => i.Kind));
        Assert.Equal(new[] { 0, 1, 2 }, seen.Items.Select(i => i.Depth));
        Assert.Equal(seen.Items[1].Id, seen.Items[2].ParentId);

        // 도구가 끝난 뒤에도 프로세스는 살아 있다 → 고아 후보로 남아 "관련 정리"의 대상이 된다.
        var after = registry.Snapshot();
        var orphan = Assert.Single(after.Items);
        Assert.True(orphan.IsOrphanCandidate);
        Assert.Equal(1, after.OrphanCandidateCount);
    }

    [Fact]
    public async Task ParallelTools_EachOwnTheirOwnChildProcess()
    {
        // 병렬 경로(RunCallAsync)의 주변 소유자 검증 — 8개가 동시에 도는 동안 소유자가 섞이면
        // 계층이 뒤엉키고, 무엇보다 "이 도구의 프로세스"를 골라 종료할 수 없게 된다.
        var probe = new FakeProcessProbe();
        probe.Add(7001, "fake", T0);
        probe.Add(7002, "fake", T0);
        var registry = new AgentActivityRegistry(probe, () => T0);

        ITool[] tools =
        [
            new ProcessSpawningTool("readA", registry, 7001, _ => { }, ToolRisk.ReadOnly),
            new ProcessSpawningTool("readB", registry, 7002, _ => { }, ToolRisk.ReadOnly),
        ];

        await RunAsync(Build(registry, ApiCalling("readA", "readB"), tools), new AgentSession());

        // 도구가 전부 끝났으므로 프로세스 2개만 남고, 각자 서로 다른 소유자를 가리켜야 한다.
        var items = registry.Snapshot().Items;
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(AgentActivityKind.ChildProcess, i.Kind));
        Assert.All(items, i => Assert.NotNull(i.ParentId));
        Assert.Equal(2, items.Select(i => i.ParentId).Distinct().Count());
    }

    [Fact]
    public async Task SubagentTurn_NestsUnderTheDelegatingTool()
    {
        // 이 테스트가 지키는 것: 주변 소유자가 async 반복자(RunAsync) 경계를 넘어 하위 턴까지 전달된다.
        // 여기가 깨지면 서브에이전트 턴이 최상위로 떠서 누가 띄웠는지 알 수 없게 된다.
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);

        AgentActivityView? seen = null;
        var sub = Build(registry, ApiCalling("inner_read"),
            new ObservingTool("inner_read", ToolRisk.ReadOnly, registry, v => seen = v));
        var outer = Build(registry, ApiCalling("delegate"), new DelegatingTool("delegate", sub));

        await RunAsync(outer, new AgentSession());

        Assert.NotNull(seen);
        Assert.Equal(
            new[]
            {
                AgentActivityKind.Turn, AgentActivityKind.Tool,   // 부모 턴 → delegate 도구
                AgentActivityKind.Turn, AgentActivityKind.Tool,   // 서브에이전트 턴 → 그 안의 도구
            },
            seen!.Items.Select(i => i.Kind));
        Assert.Equal(new[] { 0, 1, 2, 3 }, seen.Items.Select(i => i.Depth));
        Assert.Equal(seen.Items[1].Id, seen.Items[2].ParentId);
        Assert.Empty(registry.Snapshot().Items);
    }

    // ── 개별 취소 vs 전체 중지 ───────────────────────────────────────────────

    [Fact]
    public async Task CancellingOneTool_LeavesTheTurnRunning()
    {
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);
        var tool = new SelfCancellingTool("stubborn", registry, cancelTurn: false);

        var session = new AgentSession();
        var events = await RunAsync(Build(registry, ApiCalling("stubborn"), tool), session);

        // 턴은 완주해야 한다 — 도구 하나 중지가 대화를 끊으면 "개별 취소"가 아니다.
        Assert.Contains(events, e => e is AgentDone);
        Assert.DoesNotContain(events, e => e is AgentError { Code: "cancelled" });

        var toolResult = Assert.Single(session.Messages, m => m.Role == MessageRole.Tool);
        Assert.True(toolResult.IsError);
        Assert.Contains("이 도구 실행을 사용자가 중지했습니다", toolResult.Content, StringComparison.Ordinal);
        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public async Task CancellingTheTurnItem_StopsTheRun()
    {
        var registry = new AgentActivityRegistry(new FakeProcessProbe(), () => T0);
        var tool = new SelfCancellingTool("stubborn", registry, cancelTurn: true);

        var session = new AgentSession();
        var events = await RunAsync(Build(registry, ApiCalling("stubborn"), tool), session);

        // 턴 항목 중지는 기존 사용자 중지와 같은 결과여야 한다.
        Assert.Contains(events, e => e is AgentError { Code: "cancelled" });

        // 오염 방지 불변식 — 취소돼도 모든 tool_use 에 tool_result 짝이 있어야 한다.
        var toolUseIds = session.Messages
            .Where(m => m.ToolCalls is not null).SelectMany(m => m.ToolCalls!).Select(c => c.Id).ToList();
        var resultIds = session.Messages
            .Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolCallId!).ToHashSet();
        Assert.NotEmpty(toolUseIds);
        foreach (var id in toolUseIds) Assert.Contains(id, resultIds);

        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public async Task WithoutRegistry_BehaviourIsUnchanged()
    {
        // 계측이 꺼진 경로(헤드리스·기존 테스트 전부)가 종전과 동일하게 도는지 확인한다.
        var tracker = new ConcurrencyTracker();
        var tools = Enumerable.Range(0, 3)
            .Select(i => (ITool)new DelayTool($"read{i}", ToolRisk.ReadOnly, TimeSpan.Zero, tracker))
            .ToArray();

        var session = new AgentSession();
        var events = await RunAsync(Build(null, ApiCalling("read0", "read1", "read2"), tools), session);

        Assert.Contains(events, e => e is AgentDone);
        Assert.Equal(3, session.Messages.Count(m => m.Role == MessageRole.Tool));
    }
}

// ── 테스트용 도구 ────────────────────────────────────────────────────────────

/// <summary>실행 중 등기소 스냅샷을 한 장 남긴다.</summary>
internal sealed class ObservingTool(
    string name, ToolRisk risk, IAgentActivityRegistry registry, Action<AgentActivityView> capture) : ITool
{
    public string Name => name;
    public string Description => "테스트 도구";
    public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
    public ToolRisk Risk => risk;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        capture(registry.Snapshot());
        return Task.FromResult(ToolResult.Ok(name));
    }
}

/// <summary>
/// 잠깐 기다린 뒤 항목 수를 기록한다. 병렬 배치는 등록이 동기 구간에서 한꺼번에 끝나므로,
/// 조금 늦게 관측하면 "세마포어 대기 중인 호출까지" 전부 보여야 한다.
/// </summary>
internal sealed class LateObservingTool(string name, IAgentActivityRegistry registry, Action<int> capture) : ITool
{
    public string Name => name;
    public string Description => "테스트 도구";
    public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        await Task.Delay(200, ct).ConfigureAwait(false);
        capture(registry.Snapshot().Items.Count);
        return ToolResult.Ok(name);
    }
}

/// <summary>자식 프로세스를 띄운 것처럼 등기소에 등록한다(실제 프로세스는 없다).</summary>
internal sealed class ProcessSpawningTool(
    string name, IAgentActivityRegistry registry, int pid, Action<AgentActivityView> capture,
    ToolRisk risk = ToolRisk.Execute) : ITool
{
    public string Name => name;
    public string Description => "테스트 도구";
    public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
    public ToolRisk Risk => risk;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        registry.TrackDetachedProcess(
            new TrackedProcessIdentity(pid, "fake", new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero)),
            "fake.exe");
        capture(registry.Snapshot());
        return Task.FromResult(ToolResult.Ok(name));
    }
}

/// <summary>서브에이전트처럼 하위 오케스트레이터를 한 번 돌린다.</summary>
internal sealed class DelegatingTool(string name, IAgentOrchestrator sub) : ITool
{
    public string Name => name;
    public string Description => "테스트 도구";
    public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        await foreach (var _ in sub.RunAsync("서브 목표", new AgentSession(), ct: ct).ConfigureAwait(false))
        {
            // 하위 이벤트는 버린다(TaskTool 과 동일).
        }
        return ToolResult.Ok(name);
    }
}

/// <summary>
/// 사용자가 태스크 매니저에서 "중지"를 누른 상황을 도구 안에서 재현한다 —
/// 자기 항목(또는 턴 항목)의 취소를 요청하고, 토큰을 보는 대기에 들어가 실제로 풀리는지 확인한다.
/// </summary>
internal sealed class SelfCancellingTool(string name, IAgentActivityRegistry registry, bool cancelTurn) : ITool
{
    public string Name => name;
    public string Description => "테스트 도구";
    public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var items = registry.Snapshot().Items;
        var target = cancelTurn
            ? items.First(i => i.Kind == AgentActivityKind.Turn)
            : items.First(i => i.Kind == AgentActivityKind.Tool);

        registry.RequestCancel(target.Id);

        // 협조적 취소 — 토큰을 보는 대기라 곧 풀린다.
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return ToolResult.Ok(name);
    }
}
