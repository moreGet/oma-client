using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 등기소 동작. 계약:
///  (1) 등록된 항목은 어떤 경로로든 <b>반드시 해제된다</b>(예외·취소 포함) — 유령 항목이 곧 오진의 근원이다.
///  (2) 계층은 턴 → 도구 → 자식 프로세스이고, 부모가 사라진 프로세스만 "고아"다.
///  (3) 강제 종료는 신원 재확인을 통과한 프로세스에만 일어난다(PID 는 재사용된다).
///  (4) 취소는 협조적이며, 요청 시각을 남겨 "안 멈추는 항목"을 드러낸다.
/// 실제 프로세스는 하나도 띄우지 않는다 — <see cref="FakeProcessProbe"/> 로 관측·종료를 가짜로 만든다.
/// </summary>
public class AgentActivityRegistryTests
{
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;
    private static readonly DateTimeOffset T0 = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; set; } = start;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (AgentActivityRegistry Registry, FakeProcessProbe Probe, Clock Clock) Build()
    {
        var clock = new Clock(T0);
        var probe = new FakeProcessProbe();
        return (new AgentActivityRegistry(probe, () => clock.Now), probe, clock);
    }

    private static TrackedProcessIdentity Pid(int pid, string name = "cmd", DateTimeOffset? started = null)
        => new(pid, name, started ?? T0);

    // ── 등록·해제 (누수 방지) ────────────────────────────────────────────────

    [Fact]
    public void Scopes_UnregisterOnDispose()
    {
        var (registry, _, _) = Build();

        using (var turn = registry.BeginTurn(CancellationToken.None, 25))
        using (registry.BeginTool(turn.Id, CancellationToken.None, "read_file", ToolRisk.ReadOnly, NoArgs))
        {
            Assert.Equal(2, registry.Snapshot().Items.Count);
        }

        // 해제가 빠지면 목록이 계속 자란다 — 이 단정이 누수 방지의 최소선이다.
        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public void DisposedScope_LandsInHistory()
    {
        var (registry, _, clock) = Build();

        using (registry.BeginTool(null, CancellationToken.None, "grep", ToolRisk.ReadOnly, NoArgs))
            clock.Advance(TimeSpan.FromSeconds(3));

        var recent = registry.Snapshot().Recent;
        var entry = Assert.Single(recent);
        Assert.Equal("grep", entry.Title);
        Assert.Equal(AgentActivityState.Completed, entry.FinalState);
        Assert.Equal(TimeSpan.FromSeconds(3), entry.Duration);
    }

    [Fact]
    public void History_IsCapped_NewestFirst()
    {
        var (registry, _, _) = Build();

        for (var i = 0; i < 40; i++)
            registry.BeginTool(null, CancellationToken.None, $"tool{i}", ToolRisk.ReadOnly, NoArgs).Dispose();

        var recent = registry.Snapshot().Recent;
        Assert.Equal(30, recent.Count);              // 무한히 자라지 않는다
        Assert.Equal("tool39", recent[0].Title);     // 최신이 위
        Assert.Equal("tool10", recent[^1].Title);
    }

    [Fact]
    public void CancelledScope_IsRecordedAsCanceled()
    {
        var (registry, _, _) = Build();
        var scope = registry.BeginTool(null, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);

        registry.RequestCancel(scope.Id);
        scope.Dispose();

        Assert.Equal(AgentActivityState.Canceled, Assert.Single(registry.Snapshot().Recent).FinalState);
    }

    // ── 계층 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_IsDepthFirstWithDepth()
    {
        var (registry, probe, _) = Build();
        probe.Add(100, "cmd", T0);

        using var turn = registry.BeginTurn(CancellationToken.None, 25);
        using var tool = registry.BeginTool(turn.Id, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);
        using var owner = registry.EnterOwner(tool.Id);
        using var proc = registry.TrackChildProcess(Pid(100), "dir");

        var items = registry.Snapshot().Items;
        Assert.Equal(3, items.Count);
        Assert.Equal(
            new[] { AgentActivityKind.Turn, AgentActivityKind.Tool, AgentActivityKind.ChildProcess },
            items.Select(i => i.Kind));
        Assert.Equal(new[] { 0, 1, 2 }, items.Select(i => i.Depth));
        Assert.Equal(turn.Id, items[1].ParentId);
        Assert.Equal(tool.Id, items[2].ParentId);
    }

    [Fact]
    public void ParallelTools_StayUnderTheirTurn_InRegistrationOrder()
    {
        var (registry, _, _) = Build();

        using var turn = registry.BeginTurn(CancellationToken.None, 25);
        var scopes = Enumerable.Range(0, 8)
            .Select(i => registry.BeginTool(turn.Id, CancellationToken.None, $"read{i}", ToolRisk.ReadOnly, NoArgs))
            .ToList();

        var items = registry.Snapshot().Items;
        Assert.Equal(9, items.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"read{i}"), items.Skip(1).Select(i => i.Title));
        Assert.All(items.Skip(1), i => Assert.Equal(1, i.Depth));

        foreach (var s in scopes) s.Dispose();
    }

    [Fact]
    public void OrphanedItem_StillShowsAsRoot()
    {
        var (registry, probe, _) = Build();
        probe.Add(200, "notepad", T0);

        // 소유 도구가 끝난 뒤에도 남는 프로세스(start_process). 부모가 사라졌다고 목록에서 빠지면
        // 사용자는 정리해야 할 대상을 볼 수 없다.
        Guid toolId;
        using (var tool = registry.BeginTool(null, CancellationToken.None, "start_process", ToolRisk.Execute, NoArgs))
        {
            toolId = tool.Id;
            using var owner = registry.EnterOwner(tool.Id);
            registry.TrackDetachedProcess(Pid(200, "notepad"), @"C:\ws\a.exe");
        }

        var item = Assert.Single(registry.Snapshot().Items);
        Assert.Equal(AgentActivityKind.ChildProcess, item.Kind);
        Assert.Equal(0, item.Depth);
        Assert.Equal(toolId, item.ParentId);   // 부모 정보는 남아 있고, 부모 노드만 사라졌다
        Assert.True(item.IsOrphanCandidate);
    }

    [Fact]
    public void ProcessWithLiveOwner_IsNotOrphanCandidate()
    {
        var (registry, probe, _) = Build();
        probe.Add(300, "cmd", T0);

        using var tool = registry.BeginTool(null, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);
        using var owner = registry.EnterOwner(tool.Id);
        using var proc = registry.TrackChildProcess(Pid(300), "ping -t");

        Assert.False(registry.Snapshot().Items.Single(i => i.Kind == AgentActivityKind.ChildProcess).IsOrphanCandidate);
    }

    // ── 자식 프로세스 생존·프루닝 ────────────────────────────────────────────

    [Fact]
    public void Snapshot_PrunesExitedProcess()
    {
        var (registry, probe, _) = Build();
        probe.Add(400, "cmd", T0);
        registry.TrackDetachedProcess(Pid(400), "a.exe");
        Assert.Single(registry.Snapshot().Items);

        probe.Kill(400);   // 스스로 종료한 상황과 동일(관측이 사라진다)

        var view = registry.Snapshot();
        Assert.Empty(view.Items);
        Assert.Contains(view.Recent, r => r.Title.Contains("pid 400", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_DropsRecycledPidWithoutKilling()
    {
        var (registry, probe, _) = Build();
        probe.Add(500, "cmd", T0);
        registry.TrackDetachedProcess(Pid(500, "cmd"), "a.exe");

        // 우리 프로세스는 죽고 같은 PID 를 다른 프로세스가 물려받았다.
        probe.Kill(500);
        probe.Add(500, "chrome", T0.AddMinutes(10));

        Assert.Empty(registry.Snapshot().Items);
        Assert.Equal(0, probe.KillCount);              // 남의 프로세스를 건드리지 않았다
        Assert.True(probe.IsAlive(500));
    }

    [Fact]
    public void UnverifiableProcess_IsKeptButNotKillable()
    {
        var (registry, probe, _) = Build();
        probe.Add(600, "cmd", startedAt: null);        // 시작 시각 조회가 거부되는 상황
        registry.TrackDetachedProcess(Pid(600), "a.exe");

        var item = Assert.Single(registry.Snapshot().Items);
        Assert.False(item.CanKill);                    // 확신이 없으면 버튼도 주지 않는다
        Assert.False(item.IsOrphanCandidate);
    }

    // ── 강제 종료 ────────────────────────────────────────────────────────────

    [Fact]
    public void KillChildProcess_KillsVerifiedProcess()
    {
        var (registry, probe, _) = Build();
        probe.Add(700, "cmd", T0);
        registry.TrackDetachedProcess(Pid(700), "a.exe");
        var id = registry.Snapshot().Items.Single().Id;

        Assert.Equal(ProcessKillOutcome.Killed, registry.KillChildProcess(id));
        Assert.False(probe.IsAlive(700));
        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public void KillChildProcess_RefusesRecycledPid()
    {
        var (registry, probe, _) = Build();
        probe.Add(800, "cmd", T0);
        registry.TrackDetachedProcess(Pid(800, "cmd"), "a.exe");
        var id = registry.Snapshot().Items.Single().Id;

        // 사용자가 목록을 보고 있는 동안 우리 프로세스가 끝나고 PID 가 재사용됐다 — 가장 위험한 레이스다.
        probe.Kill(800);
        probe.Add(800, "chrome", T0.AddMinutes(3));
        probe.ResetCounters();

        Assert.Equal(ProcessKillOutcome.IdentityMismatch, registry.KillChildProcess(id));
        Assert.Equal(0, probe.KillCount);
        Assert.True(probe.IsAlive(800));
    }

    [Fact]
    public void KillChildProcess_ReportsUnverifiable()
    {
        var (registry, probe, _) = Build();
        probe.Add(810, "cmd", startedAt: null);
        registry.TrackDetachedProcess(Pid(810), "a.exe");
        var id = registry.Snapshot().Items.Single().Id;

        Assert.Equal(ProcessKillOutcome.Unverifiable, registry.KillChildProcess(id));
        Assert.Equal(0, probe.KillCount);
    }

    [Fact]
    public void KillChildProcess_ReportsAlreadyExited()
    {
        var (registry, probe, _) = Build();
        probe.Add(820, "cmd", T0);
        registry.TrackDetachedProcess(Pid(820), "a.exe");
        var id = registry.Snapshot().Items.Single().Id;
        probe.Kill(820);
        probe.ResetCounters();

        Assert.Equal(ProcessKillOutcome.AlreadyExited, registry.KillChildProcess(id));
    }

    [Fact]
    public void KillChildProcess_OnUnknownId_IsNotTracked()
    {
        var (registry, _, _) = Build();
        Assert.Equal(ProcessKillOutcome.NotTracked, registry.KillChildProcess(Guid.NewGuid()));
    }

    [Fact]
    public void KillChildProcess_RefusesNonProcessItem()
    {
        var (registry, _, _) = Build();
        using var turn = registry.BeginTurn(CancellationToken.None, 25);

        // 턴·도구에 "강제 종료"는 존재하지 않는다(관리 스레드를 죽일 수단이 없다).
        Assert.Equal(ProcessKillOutcome.NotTracked, registry.KillChildProcess(turn.Id));
    }

    // ── 고아 정리 ────────────────────────────────────────────────────────────

    [Fact]
    public void SweepOrphans_KillsOnlyOrphans()
    {
        var (registry, probe, _) = Build();
        probe.Add(900, "cmd", T0);
        probe.Add(901, "cmd", T0);

        // 900: 소유 도구가 살아 있다 → 정리 금지. 901: 소유 도구가 끝났다 → 고아.
        using var liveTool = registry.BeginTool(null, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);
        using (registry.EnterOwner(liveTool.Id))
            registry.TrackDetachedProcess(Pid(900), "살아 있는 소유자");

        using (var doneTool = registry.BeginTool(null, CancellationToken.None, "start_process", ToolRisk.Execute, NoArgs))
        using (registry.EnterOwner(doneTool.Id))
            registry.TrackDetachedProcess(Pid(901), "끝난 소유자");

        var result = registry.SweepOrphans();

        Assert.Equal(1, result.Killed);
        Assert.True(probe.IsAlive(900));    // 실행 중인 명령을 죽이면 안 된다
        Assert.False(probe.IsAlive(901));
        Assert.Contains(result.Notes, n => n.Contains("901", StringComparison.Ordinal));
    }

    [Fact]
    public void SweepOrphans_NeverKillsRecycledPid()
    {
        var (registry, probe, _) = Build();
        using (var tool = registry.BeginTool(null, CancellationToken.None, "start_process", ToolRisk.Execute, NoArgs))
        using (registry.EnterOwner(tool.Id))
            registry.TrackDetachedProcess(Pid(910, "cmd"), "a.exe");

        probe.Add(910, "chrome", T0.AddMinutes(7));   // PID 재사용 — 남의 프로세스
        probe.ResetCounters();

        var result = registry.SweepOrphans();

        Assert.Equal(0, result.Killed);
        Assert.Equal(0, probe.KillCount);
        Assert.True(probe.IsAlive(910));
        Assert.Contains(result.Notes, n => n.Contains("재사용", StringComparison.Ordinal));
        Assert.Empty(registry.Snapshot().Items);      // 추적만 버린다
    }

    [Fact]
    public void SweepOrphans_SkipsUnverifiable()
    {
        var (registry, probe, _) = Build();
        probe.Add(920, "cmd", startedAt: null);
        using (var tool = registry.BeginTool(null, CancellationToken.None, "start_process", ToolRisk.Execute, NoArgs))
        using (registry.EnterOwner(tool.Id))
            registry.TrackDetachedProcess(Pid(920), "a.exe");

        var result = registry.SweepOrphans();

        Assert.Equal(0, result.Killed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, probe.KillCount);
        Assert.Single(registry.Snapshot().Items);     // 확인 불가는 남겨 둔다(사용자가 직접 판단)
    }

    [Fact]
    public void SweepOrphans_CountsAlreadyExited()
    {
        var (registry, probe, _) = Build();
        using (var tool = registry.BeginTool(null, CancellationToken.None, "start_process", ToolRisk.Execute, NoArgs))
        using (registry.EnterOwner(tool.Id))
            registry.TrackDetachedProcess(Pid(930), "a.exe");   // probe 에 없음 = 이미 종료

        var result = registry.SweepOrphans();

        Assert.Equal(1, result.AlreadyExited);
        Assert.Equal(0, result.Killed);
        Assert.Empty(registry.Snapshot().Items);
    }

    [Fact]
    public void TrackDetachedProcess_PrunesDeadEntriesAtCap()
    {
        var (registry, probe, _) = Build();

        // 헤드리스처럼 Snapshot 을 아무도 호출하지 않는 호스트에서도 표가 무한히 자라면 안 된다.
        for (var i = 0; i < 70; i++)
        {
            probe.Add(1000 + i, "cmd", T0);
            registry.TrackDetachedProcess(Pid(1000 + i), "a.exe");
            probe.Kill(1000 + i);   // 즉시 종료 → 다음 등록에서 걷힐 수 있는 상태
        }

        Assert.True(registry.Snapshot().Items.Count < 70,
            "죽은 프로세스 항목이 상한을 넘어서도 걷히지 않았다");
    }

    // ── 취소 ────────────────────────────────────────────────────────────────

    [Fact]
    public void RequestCancel_SignalsOnlyThatItemsToken()
    {
        var (registry, _, _) = Build();
        using var turn = registry.BeginTurn(CancellationToken.None, 25);
        using var toolA = registry.BeginTool(turn.Id, CancellationToken.None, "readA", ToolRisk.ReadOnly, NoArgs);
        using var toolB = registry.BeginTool(turn.Id, CancellationToken.None, "readB", ToolRisk.ReadOnly, NoArgs);

        Assert.True(registry.RequestCancel(toolA.Id));

        Assert.True(toolA.Token.IsCancellationRequested);
        Assert.False(toolB.Token.IsCancellationRequested);   // 형제를 끌고 내려가면 안 된다
        Assert.False(turn.Token.IsCancellationRequested);    // 턴도 계속 가야 한다
    }

    [Fact]
    public void CancellingTurn_CascadesToItsTools()
    {
        var (registry, _, _) = Build();
        using var turn = registry.BeginTurn(CancellationToken.None, 25);
        // 부모 토큰으로 turn.Token 을 넘기는 것이 계약이다(오케스트레이터가 그렇게 부른다) —
        // 이것을 CancellationToken.None 으로 넘기면 계층은 보이지만 취소가 아래로 내려가지 않는다.
        using var tool = registry.BeginTool(turn.Id, turn.Token, "read", ToolRisk.ReadOnly, NoArgs);

        // 도구 토큰은 턴 토큰에 연결돼 있다 — 턴을 접으면 아래가 함께 풀린다.
        registry.RequestCancel(turn.Id);

        Assert.True(turn.Token.IsCancellationRequested);
        Assert.True(tool.Token.IsCancellationRequested);
    }

    [Fact]
    public void ExternalToken_CascadesIntoRegisteredItems()
    {
        var (registry, _, _) = Build();
        using var external = new CancellationTokenSource();

        using var turn = registry.BeginTurn(external.Token, 25);
        using var tool = registry.BeginTool(turn.Id, turn.Token, "read", ToolRisk.ReadOnly, NoArgs);

        // 기존 사용자 중지(_cts)·앱 종료 경로가 여전히 전부를 접을 수 있어야 한다.
        external.Cancel();

        Assert.True(turn.Token.IsCancellationRequested);
        Assert.True(tool.Token.IsCancellationRequested);
    }

    [Fact]
    public void RequestCancel_KeepsFirstRequestTime()
    {
        var (registry, _, clock) = Build();
        using var tool = registry.BeginTool(null, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);

        registry.RequestCancel(tool.Id);
        clock.Advance(TimeSpan.FromSeconds(4));
        registry.RequestCancel(tool.Id);   // 연타로 시각이 리셋되면 "안 멈추고 있다"를 영원히 못 본다

        var item = Assert.Single(registry.Snapshot().Items);
        Assert.Equal(TimeSpan.FromSeconds(4), item.CancelRequestedFor);
        Assert.Equal(AgentActivityState.CancelRequested, item.State);
        Assert.False(item.CanCancel);      // 이미 요청했다
    }

    [Fact]
    public void StalledCancel_IsSurfaced()
    {
        var (registry, _, clock) = Build();
        using var tool = registry.BeginTool(null, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);

        registry.RequestCancel(tool.Id);
        clock.Advance(ActivityHealthRules.CancelGrace + TimeSpan.FromSeconds(1));

        var view = registry.Snapshot();
        Assert.Equal(AgentActivityHealth.CancelStalled, view.Items.Single().Health);
        Assert.Equal(1, view.CancelStalledCount);
    }

    [Fact]
    public void RequestCancel_OnProcessItem_IsRefused()
    {
        var (registry, probe, _) = Build();
        probe.Add(1200, "cmd", T0);
        registry.TrackDetachedProcess(Pid(1200), "a.exe");
        var id = registry.Snapshot().Items.Single().Id;

        // 프로세스에는 협조적 취소가 없다 — 강제 종료가 유일한 수단이고 UI 도 그렇게 안내해야 한다.
        Assert.False(registry.RequestCancel(id));
        Assert.Null(registry.Snapshot().Items.Single().CancelRequestedFor);
    }

    [Fact]
    public void RequestCancel_OnUnknownId_IsFalse()
    {
        var (registry, _, _) = Build();
        Assert.False(registry.RequestCancel(Guid.NewGuid()));
    }

    [Fact]
    public void CancelAll_SignalsEveryCancellableItem()
    {
        var (registry, probe, _) = Build();
        probe.Add(1300, "cmd", T0);

        using var turn = registry.BeginTurn(CancellationToken.None, 25);
        using var toolA = registry.BeginTool(turn.Id, turn.Token, "readA", ToolRisk.ReadOnly, NoArgs);
        using var toolB = registry.BeginTool(turn.Id, turn.Token, "readB", ToolRisk.ReadOnly, NoArgs);
        registry.TrackDetachedProcess(Pid(1300), "a.exe");   // 토큰 없음 → 집계에 포함되지 않는다

        Assert.Equal(3, registry.CancelAll());
        Assert.True(turn.Token.IsCancellationRequested);
        Assert.True(toolA.Token.IsCancellationRequested);
        Assert.True(toolB.Token.IsCancellationRequested);
        Assert.Equal(0, probe.KillCount);   // 전역 중지는 프로세스를 죽이지 않는다(별도 조작이다)
    }

    // ── 파생값·요약 ──────────────────────────────────────────────────────────

    [Fact]
    public void Elapsed_FollowsInjectedClock()
    {
        var (registry, _, clock) = Build();
        using var tool = registry.BeginTool(null, CancellationToken.None, "read", ToolRisk.ReadOnly, NoArgs);

        clock.Advance(TimeSpan.FromMinutes(2));

        var item = Assert.Single(registry.Snapshot().Items);
        Assert.Equal(TimeSpan.FromMinutes(2), item.Elapsed);
        Assert.Equal(AgentActivityHealth.LongRunning, item.Health);
    }

    [Fact]
    public void ToolItem_CarriesRiskAndArgumentSummary()
    {
        var (registry, _, _) = Build();
        var args = JsonDocument.Parse("""{"shell":"cmd","command":"dir C:\\"}""").RootElement;
        using var tool = registry.BeginTool(null, CancellationToken.None, "run_command", ToolRisk.Execute, args);

        var item = Assert.Single(registry.Snapshot().Items);
        Assert.Equal(ToolRisk.Execute, item.Risk);
        Assert.Equal("dir C:\\", item.Detail);
        Assert.Null(item.Pid);
    }

    [Fact]
    public void TurnItem_CarriesIterationProgress()
    {
        var (registry, _, _) = Build();
        using var turn = registry.BeginTurn(CancellationToken.None, 25);
        turn.ReportIteration(3);

        var item = Assert.Single(registry.Snapshot().Items);
        Assert.Equal(3, item.Iteration);
        Assert.Equal(25, item.MaxIterations);
        Assert.Equal(1, registry.Snapshot().RunningCount);
    }

    [Fact]
    public void ProcessItem_CarriesPidAndOrphanCount()
    {
        var (registry, probe, _) = Build();
        probe.Add(1400, "notepad", T0);
        registry.TrackDetachedProcess(Pid(1400, "notepad"), @"C:\ws\a.exe");

        var view = registry.Snapshot();
        var item = view.Items.Single();
        Assert.Equal(1400, item.Pid);
        Assert.Contains("notepad", item.Title, StringComparison.Ordinal);
        Assert.True(item.CanKill);
        Assert.Equal(1, view.OrphanCandidateCount);
    }

    // ── 이벤트 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Changed_FiresOnStructuralChangesOnly()
    {
        var (registry, _, clock) = Build();
        var fired = 0;
        registry.Changed += (_, _) => fired++;

        var tool = registry.BeginTool(null, CancellationToken.None, "read", ToolRisk.ReadOnly, NoArgs);
        Assert.Equal(1, fired);

        clock.Advance(TimeSpan.FromMinutes(1));
        registry.Snapshot();
        // 경과 시간만 흘렀을 때는 발화하지 않는다 — 1초 단위 갱신은 표시 계층의 책임이다.
        Assert.Equal(1, fired);

        registry.RequestCancel(tool.Id);
        Assert.Equal(2, fired);

        tool.Dispose();
        Assert.Equal(3, fired);
    }

    [Fact]
    public void SubscriberException_DoesNotBreakInstrumentation()
    {
        var (registry, _, _) = Build();
        registry.Changed += (_, _) => throw new InvalidOperationException("UI 폭발");

        // 표시 실패가 에이전트 실행을 죽이면 안 된다(LoopController.Emit 과 같은 정책).
        using var tool = registry.BeginTool(null, CancellationToken.None, "read", ToolRisk.ReadOnly, NoArgs);
        Assert.Single(registry.Snapshot().Items);
    }
}

/// <summary>
/// 가짜 프로세스 세계. 실제 프로세스를 절대 띄우거나 죽이지 않으면서 관측·종료 경로를 전부 돌린다 —
/// 테스트가 남의 프로세스를 죽이는 사고를 원천 차단하는 경계이기도 하다.
/// </summary>
internal sealed class FakeProcessProbe : IProcessProbe
{
    private readonly Dictionary<int, ProcessObservation> _alive = [];

    public int KillCount { get; private set; }

    public void Add(int pid, string name, DateTimeOffset? startedAt)
        => _alive[pid] = new ProcessObservation(pid, name, startedAt);

    public bool IsAlive(int pid) => _alive.ContainsKey(pid);

    public void Kill(int pid) => _alive.Remove(pid);

    public void ResetCounters() => KillCount = 0;

    public ProcessObservation? Observe(int pid) => _alive.TryGetValue(pid, out var p) ? p : null;

    public bool TryKill(int pid, out string? error)
    {
        error = null;
        KillCount++;
        return _alive.Remove(pid);
    }
}
