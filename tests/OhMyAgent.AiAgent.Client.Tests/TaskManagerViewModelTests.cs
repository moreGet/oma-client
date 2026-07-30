using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Loop;
using OhMyAgent.AiAgent.Client.ViewModels;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 작업 관리(태스크 매니저) 목록의 <b>병합 갱신</b>. 창이 열려 있는 동안 1초마다 스냅샷을 다시 뜨므로,
/// 컬렉션을 통째로 갈아 끼우면 매초 스크롤이 초기화되고 마우스 아래 있던 버튼이 사라져 클릭이 빗나간다.
/// "살아 있는 행은 같은 인스턴스를 유지한다"가 그 방지선이고, 여기서 못박는다.
/// </summary>
public class ActivityRowSyncTests
{
    private sealed class Row(Guid id, string text) : IKeyedRow
    {
        public Guid Id { get; } = id;
        public string Text { get; set; } = text;
    }

    private sealed record Model(Guid Id, string Text);

    private static void Sync(ObservableCollection<Row> target, params Model[] source)
        => ActivityRowSync.Sync(
            target, source, m => m.Id, m => new Row(m.Id, m.Text), (row, m) => row.Text = m.Text);

    [Fact]
    public void Sync_IntoEmptyCollection_AddsInSourceOrder()
    {
        var a = new Model(Guid.NewGuid(), "a");
        var b = new Model(Guid.NewGuid(), "b");
        var target = new ObservableCollection<Row>();

        Sync(target, a, b);

        Assert.Equal(new[] { a.Id, b.Id }, target.Select(r => r.Id));
    }

    [Fact]
    public void Sync_KeepsSameInstances_AndUpdatesInPlace()
    {
        var a = new Model(Guid.NewGuid(), "a");
        var target = new ObservableCollection<Row>();
        Sync(target, a);
        var first = target[0];

        Sync(target, a with { Text = "a2" });

        // 인스턴스가 바뀌면 WPF 가 행을 다시 만들고 스크롤·호버가 매초 튄다.
        Assert.Same(first, target[0]);
        Assert.Equal("a2", target[0].Text);
    }

    [Fact]
    public void Sync_RemovesRowsThatDisappeared()
    {
        var a = new Model(Guid.NewGuid(), "a");
        var b = new Model(Guid.NewGuid(), "b");
        var target = new ObservableCollection<Row>();
        Sync(target, a, b);

        Sync(target, a);

        Assert.Equal(new[] { a.Id }, target.Select(r => r.Id));
    }

    [Fact]
    public void Sync_ReordersByMoving_WithoutRecreating()
    {
        var a = new Model(Guid.NewGuid(), "a");
        var b = new Model(Guid.NewGuid(), "b");
        var target = new ObservableCollection<Row>();
        Sync(target, a, b);
        var rowA = target[0];
        var rowB = target[1];

        Sync(target, b, a);

        Assert.Same(rowB, target[0]);
        Assert.Same(rowA, target[1]);
    }

    [Fact]
    public void Sync_InsertsInTheMiddle_WithoutTouchingNeighbours()
    {
        var a = new Model(Guid.NewGuid(), "a");
        var c = new Model(Guid.NewGuid(), "c");
        var target = new ObservableCollection<Row>();
        Sync(target, a, c);
        var rowA = target[0];
        var rowC = target[1];

        var b = new Model(Guid.NewGuid(), "b");
        Sync(target, a, b, c);

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, target.Select(r => r.Id));
        Assert.Same(rowA, target[0]);
        Assert.Same(rowC, target[2]);   // 병렬 도구가 하나 끼어들어도 나머지 행은 그대로여야 한다
    }

    [Fact]
    public void Sync_ToEmptySource_ClearsEverything()
    {
        var target = new ObservableCollection<Row>();
        Sync(target, new Model(Guid.NewGuid(), "a"), new Model(Guid.NewGuid(), "b"));

        Sync(target);

        Assert.Empty(target);
    }
}

/// <summary>
/// 사용자 문구 규약. .NET 에서 <b>관리 스레드 강제 종료는 불가능</b>하므로 "스레드를 종료했다"는 표현이
/// 하나라도 새어 나가면 사용자에게 거짓을 말하는 것이 된다. 그리고 강제 종료가 <b>일어나지 않은</b> 경우
/// (PID 재사용·확인 불가)는 반드시 그 사실을 말해야 한다 — 죽은 줄 알면 프로세스가 방치된다.
/// </summary>
public class TaskManagerTextTests
{
    /// <summary>사용자에게 나가는 모든 문구를 한 자리에 모은다(규약 검사 대상).</summary>
    private static IEnumerable<string> AllUserFacingText()
    {
        foreach (var kind in Enum.GetValues<AgentActivityKind>())
            yield return TaskManagerText.KindText(kind);

        foreach (var state in Enum.GetValues<AgentActivityState>())
            yield return TaskManagerText.StateText(state);

        foreach (var health in Enum.GetValues<AgentActivityHealth>())
            yield return TaskManagerText.RowNote(health, TimeSpan.FromSeconds(7));

        foreach (var outcome in Enum.GetValues<ProcessKillOutcome>())
            yield return TaskManagerText.KillOutcome(outcome, "notepad (pid 1234)");

        yield return TaskManagerText.Summary(0, 0, 0);
        yield return TaskManagerText.Summary(3, 1, 2);
        yield return TaskManagerText.CancelResult(true, "read_file");
        yield return TaskManagerText.CancelResult(false, "notepad");
        yield return TaskManagerText.CancelAllResult(0);
        yield return TaskManagerText.CancelAllResult(3);
        yield return TaskManagerText.SweepResult(OrphanSweepResult.None);
        yield return TaskManagerText.SweepResult(new OrphanSweepResult(2, 1, 0, 1, ["PID 재사용 — 종료하지 않음"]));
        yield return TaskManagerText.IterationText(3, 25);
    }

    [Fact]
    public void NoUserFacingText_ClaimsToKillThreads()
    {
        foreach (var text in AllUserFacingText())
        {
            Assert.DoesNotContain("스레드", text);
            Assert.DoesNotContain("쓰레드", text);
            Assert.DoesNotContain("thread", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StateText_UsesStopWording_ForCancellation()
    {
        // 도구·턴에 쓰는 것은 협조적 취소 요청이고, 사용자에게는 "중지"로 부른다(계약 §4-6).
        Assert.Equal("중지 요청", TaskManagerText.StateText(AgentActivityState.CancelRequested));
        Assert.Equal("중지됨", TaskManagerText.StateText(AgentActivityState.Canceled));
    }

    [Fact]
    public void KindText_HasNoThreadKind()
    {
        // 모델에 Thread 종류가 없다는 사실이 문구에도 그대로 반영돼야 한다.
        var kinds = Enum.GetValues<AgentActivityKind>().Select(TaskManagerText.KindText).ToArray();
        Assert.Equal(new[] { "턴", "도구", "프로세스" }, kinds);
    }

    [Fact]
    public void KillOutcome_ForRecycledPid_SaysItDidNotKill()
    {
        var text = TaskManagerText.KillOutcome(ProcessKillOutcome.IdentityMismatch, "notepad");

        Assert.Contains("종료하지 않았습니다", text);
        Assert.Contains("재사용", text);
    }

    [Fact]
    public void KillOutcome_ForUnverifiable_SaysItDidNotKill()
    {
        Assert.Contains("종료하지 않았습니다", TaskManagerText.KillOutcome(ProcessKillOutcome.Unverifiable, "x"));
    }

    [Fact]
    public void CancelAllResult_WithNothingSignalled_DoesNotClaimSuccess()
    {
        Assert.Equal("중지할 작업이 없습니다.", TaskManagerText.CancelAllResult(0));
        Assert.Contains("3개", TaskManagerText.CancelAllResult(3));
    }

    [Fact]
    public void CancelResult_AlwaysWarnsItMayNotStopImmediately()
    {
        // 눌러도 사라지지 않는 것이 정상이라는 사실을 말하지 않으면 사용자는 클릭이 씹혔다고 본다.
        Assert.Contains("즉시 끝나지 않을 수 있습니다", TaskManagerText.CancelResult(true, "read_file"));
    }

    [Fact]
    public void SweepResult_ShowsWhatWasLeftAlone()
    {
        var text = TaskManagerText.SweepResult(
            new OrphanSweepResult(3, 1, 1, 1, ["PID 재사용 — 종료하지 않음", "확인 불가"]));

        Assert.Contains("건드리지 않음 1개", text);
        Assert.Contains("PID 재사용 — 종료하지 않음", text);   // 숫자만 주면 "왜 안 지워졌지"가 남는다
        Assert.Contains("확인 불가", text);
    }

    [Fact]
    public void SweepResult_WithOnlySkippedItems_StillExplainsWhy()
    {
        // Inspected 는 "실제로 종료를 시도한 개수"라서, 전부 PID 재사용·확인 불가로 건드리지 않으면 0 이 된다.
        // 그때 "정리할 프로세스가 없습니다" 로만 답하면 사용자는 화면에 남아 있는 프로세스를 보면서
        // 왜 안 지워졌는지 알 수 없다 — 건드리지 않은 이유(Notes)가 이 경우에 가장 중요하다.
        var text = TaskManagerText.SweepResult(
            new OrphanSweepResult(0, 0, 0, 2, ["pid 1234: PID 가 재사용됨", "pid 5678: 시작 시각을 확인할 수 없어"]));

        Assert.Contains("건드리지 않음 2개", text);
        Assert.Contains("PID 가 재사용됨", text);
        Assert.Contains("시작 시각을 확인할 수 없어", text);
    }

    [Fact]
    public void SweepResult_WithNothingToDo_ExplainsScope()
    {
        var text = TaskManagerText.SweepResult(OrphanSweepResult.None);

        Assert.Contains("없습니다", text);
        Assert.Contains("우리가 띄웠고", text);   // 시스템 프로세스를 훑지 않는다는 사실을 알려야 한다
    }

    [Fact]
    public void Summary_WithNothingRunning_SaysSo_InsteadOfZeroes()
    {
        Assert.Equal("진행 중인 작업이 없습니다.", TaskManagerText.Summary(0, 0, 0));
    }

    [Fact]
    public void Summary_MentionsStalledAndOrphansOnlyWhenPresent()
    {
        Assert.Equal("진행 중 2개", TaskManagerText.Summary(2, 0, 0));

        var full = TaskManagerText.Summary(2, 1, 3);
        Assert.Contains("중지 지연 1개", full);
        Assert.Contains("정리 대상 프로세스 3개", full);
    }

    [Fact]
    public void RowNote_ForStalledCancel_PointsAtTheOnlyRemainingLever()
    {
        // 협조적 중지가 통하지 않을 때 실제로 손쓸 수 있는 유일한 수단이 자식 프로세스 강제 종료다.
        var note = TaskManagerText.RowNote(AgentActivityHealth.CancelStalled, TimeSpan.FromSeconds(12));

        Assert.Contains("강제 종료", note);
        Assert.Contains("12초", note);      // 얼마나 안 멈추고 있는지가 보여야 한다
    }

    [Fact]
    public void RowNote_ForNormal_IsEmpty()
        => Assert.Equal("", TaskManagerText.RowNote(AgentActivityHealth.Normal, null));

    [Fact]
    public void IterationText_IsEmpty_WhenNotATurn()
    {
        Assert.Equal("", TaskManagerText.IterationText(null, null));
        Assert.Equal("반복 3/25", TaskManagerText.IterationText(3, 25));
    }
}

/// <summary>
/// 태스크 매니저 VM. 계약:
///  (1) 안전 판정을 복제하지 않는다 — <c>CanCancel</c>/<c>CanKill</c> 을 Core 값 그대로 옮긴다.
///  (2) 프로세스 행에는 "중지"를 두지 않는다(<c>RequestCancel</c> 이 항상 false 다).
///  (3) 트리를 다시 조립하지 않는다 — 스냅샷의 <c>Depth</c> 를 그대로 쓴다.
///  (4) 창이 닫히면(<c>Detach</c>) 구독이 끊긴다.
///  (5) 파괴적 동작 셋은 확인을 거치고, 거절하면 아무 일도 일어나지 않는다.
/// 실제 프로세스는 하나도 띄우지 않는다(<see cref="FakeProcessProbe"/>).
/// </summary>
public class TaskManagerViewModelTests
{
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;
    private static readonly DateTimeOffset T0 = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; set; } = start;
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>확인 대화상자 대역. VM 이 <c>MessageBox</c> 를 직접 부르지 않기에 테스트가 가능하다.</summary>
    private sealed class FakeDialogs : IDialogService
    {
        public bool ConfirmAnswer { get; set; } = true;
        public int ConfirmCount { get; private set; }
        public string LastConfirmMessage { get; private set; } = "";

        public void ShowMessage(string title, string message, DialogSeverity severity = DialogSeverity.Information) { }

        public string? PromptText(string title, string message, string defaultValue = "", bool multiline = false) => null;

        public bool Confirm(string title, string message, DialogSeverity severity = DialogSeverity.Warning)
        {
            ConfirmCount++;
            LastConfirmMessage = message;
            return ConfirmAnswer;
        }
    }

    private sealed class StubLoop : ILoopController
    {
        public bool IsRunning { get; set; }
        public LoopStatusSnapshot Status { get; set; } = LoopStatusSnapshot.Idle;
        public LoopStopReason? LastStopReason { get; private set; }

        public event EventHandler<LoopEvent>? LoopChanged;

        public bool TryStart(LoopStartRequest request, LoopTurnRunner runner, CancellationToken externalCt, out string? error)
        {
            error = null;
            return false;
        }

        public void Stop(LoopStopReason reason) { LastStopReason = reason; IsRunning = false; }

        public Task StopAndWaitAsync(LoopStopReason reason, TimeSpan? timeout = null)
        {
            Stop(reason);
            return Task.CompletedTask;
        }

        public void Dispose() { }

        public void Raise(LoopEvent e) => LoopChanged?.Invoke(this, e);
    }

    private sealed record Harness(
        TaskManagerViewModel Vm,
        AgentActivityRegistry Registry,
        FakeProcessProbe Probe,
        StubLoop Loop,
        FakeDialogs Dialogs,
        Clock Clock);

    /// <summary>ImmediateUiDispatcher 라 이벤트 → 갱신이 동기적으로 일어나 단정이 성립한다.</summary>
    private static Harness Build()
    {
        var clock = new Clock(T0);
        var probe = new FakeProcessProbe();
        var registry = new AgentActivityRegistry(probe, () => clock.Now);
        var loop = new StubLoop();
        var dialogs = new FakeDialogs();
        var vm = new TaskManagerViewModel(registry, loop, new ImmediateUiDispatcher(), dialogs);
        return new Harness(vm, registry, probe, loop, dialogs, clock);
    }

    private static TrackedProcessIdentity Pid(int pid, string name = "cmd", DateTimeOffset? started = null)
        => new(pid, name, started ?? T0);

    // ── 투영 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Attach_ProjectsSnapshot_Immediately()
    {
        var h = Build();
        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);

        h.Vm.Attach();

        // 창이 뜬 첫 프레임이 비어 보이면 "고장난 창"으로 읽힌다.
        var row = Assert.Single(h.Vm.Items);
        Assert.Equal("에이전트 턴", row.Title);
        Assert.Equal(1, h.Vm.RunningCount);
        Assert.True(h.Vm.HasItems);
        h.Vm.Dispose();
    }

    [Fact]
    public void RegistryChanged_RefreshesWithoutTheTimer()
    {
        var h = Build();
        h.Vm.Attach();
        Assert.Empty(h.Vm.Items);

        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);

        // 구조 변화는 이벤트로 즉시 온다(경과 시간만 1초 타이머 몫이다).
        Assert.Single(h.Vm.Items);
        h.Vm.Dispose();
    }

    [Fact]
    public void Refresh_UsesSnapshotDepth_WithoutRebuildingTheTree()
    {
        var h = Build();
        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);
        using var tool = h.Registry.BeginTool(turn.Id, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);
        using var proc = h.Registry.TrackChildProcess(Pid(1234), "dir");
        h.Probe.Add(1234, "cmd", T0);

        h.Vm.Attach();

        // 스냅샷이 이미 깊이 우선 정렬 + 깊이 계산을 끝내 온다 — 뷰는 들여쓰기만 한다.
        Assert.Equal(new[] { 0, 1 }, h.Vm.Items.Take(2).Select(r => r.Depth));
        h.Vm.Dispose();
    }

    [Fact]
    public void Refresh_PreservesRowInstances_AcrossTicks()
    {
        var h = Build();
        using var tool = h.Registry.BeginTool(null, CancellationToken.None, "grep", ToolRisk.ReadOnly, NoArgs);
        h.Vm.Attach();
        var first = h.Vm.Items[0];

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Vm.RefreshCommand.Execute(null);

        // 통째로 갈아 끼우면 매초 스크롤과 마우스 아래 버튼이 사라진다.
        Assert.Same(first, h.Vm.Items[0]);
        Assert.Equal("5초", first.ElapsedText);   // 표기는 LoopIntervalParser.Format 재사용
        h.Vm.Dispose();
    }

    [Fact]
    public void ToolRow_OffersStop_ProcessRow_OffersOnlyForceKill()
    {
        var h = Build();
        using var tool = h.Registry.BeginTool(null, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);
        h.Probe.Add(4321, "cmd", T0);
        using var proc = h.Registry.TrackChildProcess(Pid(4321), "dir");

        h.Vm.Attach();

        var toolRow = h.Vm.Items.Single(r => r.Kind == AgentActivityKind.Tool);
        var procRow = h.Vm.Items.Single(r => r.Kind == AgentActivityKind.ChildProcess);

        Assert.True(toolRow.SupportsCancel);
        Assert.True(toolRow.CanCancel);
        Assert.False(toolRow.IsProcess);

        // 프로세스에 협조적 취소는 존재하지 않는다 — "중지" 버튼을 두면 눌러도 아무 일이 없다.
        Assert.False(procRow.SupportsCancel);
        Assert.True(procRow.IsProcess);
        Assert.True(procRow.CanKill);
        h.Vm.Dispose();
    }

    [Fact]
    public void ProcessRow_WithUnverifiableIdentity_CannotBeKilled()
    {
        var h = Build();
        h.Probe.Add(5555, "cmd", null);   // 시작 시각을 못 읽는 프로세스 = Unverifiable
        h.Registry.TrackDetachedProcess(new TrackedProcessIdentity(5555, "cmd", null), null);

        h.Vm.Attach();

        // 안전 판정은 Core 가 한다 — VM 이 다시 계산하지 않고 그대로 옮기는지 본다.
        var row = Assert.Single(h.Vm.Items);
        Assert.False(row.CanKill);
        h.Vm.Dispose();
    }

    // ── 조작 ─────────────────────────────────────────────────────────────

    [Fact]
    public void CancelItem_SignalsOnlyThatItem_AndNeedsNoConfirmation()
    {
        var h = Build();
        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);
        using var tool = h.Registry.BeginTool(turn.Id, CancellationToken.None, "read_file", ToolRisk.ReadOnly, NoArgs);
        h.Vm.Attach();

        var toolRow = h.Vm.Items.Single(r => r.Kind == AgentActivityKind.Tool);
        h.Vm.CancelItemCommand.Execute(toolRow);

        Assert.True(tool.Token.IsCancellationRequested);
        Assert.False(turn.Token.IsCancellationRequested);   // 도구 하나 중지가 턴을 끊지 않는다
        Assert.Equal(0, h.Dialogs.ConfirmCount);            // 되돌릴 수 있는 성격이라 확인을 끼우지 않는다
        Assert.Contains("즉시 끝나지 않을 수 있습니다", h.Vm.ActionResultText);
        h.Vm.Dispose();
    }

    [Fact]
    public void KillItem_WhenUserDeclines_DoesNotTouchTheProcess()
    {
        var h = Build();
        h.Probe.Add(777, "cmd", T0);
        using var proc = h.Registry.TrackChildProcess(Pid(777), null);
        h.Vm.Attach();
        h.Dialogs.ConfirmAnswer = false;

        h.Vm.KillItemCommand.Execute(h.Vm.Items.Single());

        Assert.Equal(1, h.Dialogs.ConfirmCount);
        Assert.True(h.Probe.IsAlive(777));      // 거절했으면 프로세스는 살아 있어야 한다
        Assert.Equal("", h.Vm.ActionResultText);
        h.Vm.Dispose();
    }

    [Fact]
    public void KillItem_WhenConfirmed_KillsAndReports()
    {
        var h = Build();
        h.Probe.Add(778, "cmd", T0);
        using var proc = h.Registry.TrackChildProcess(Pid(778), null);
        h.Vm.Attach();

        h.Vm.KillItemCommand.Execute(h.Vm.Items.Single());

        Assert.False(h.Probe.IsAlive(778));
        Assert.Contains("강제 종료했습니다", h.Vm.ActionResultText);
        h.Vm.Dispose();
    }

    [Fact]
    public void KillItem_OnRecycledPid_TellsTheUserItDidNotKill()
    {
        var h = Build();
        h.Probe.Add(779, "cmd", T0);
        using var proc = h.Registry.TrackChildProcess(Pid(779, "cmd", T0), null);
        h.Vm.Attach();
        var row = Assert.Single(h.Vm.Items);

        // 사용자가 목록을 보는 동안 OS 가 그 PID 를 다른 프로세스에 재사용한 상황 — 가장 위험한 경로다.
        // (스냅샷이 재사용을 먼저 알아채면 행이 사라지므로, 목록을 그린 뒤에 재사용을 일으킨다.)
        h.Probe.Add(779, "notepad", T0.AddMinutes(5));
        h.Vm.KillItemCommand.Execute(row);

        Assert.Contains("종료하지 않았습니다", h.Vm.ActionResultText);
        h.Vm.Dispose();
    }

    [Fact]
    public void CancelAll_IsDisabledWhenIdle_AndReportsSignalCount()
    {
        var h = Build();
        h.Vm.Attach();

        Assert.False(h.Vm.CancelAllCommand.CanExecute(null));   // 중지할 것이 없으면 버튼이 회색이어야 한다

        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);
        using var tool = h.Registry.BeginTool(turn.Id, CancellationToken.None, "grep", ToolRisk.ReadOnly, NoArgs);

        Assert.True(h.Vm.CancelAllCommand.CanExecute(null));

        h.Vm.CancelAllCommand.Execute(null);

        Assert.Equal(1, h.Dialogs.ConfirmCount);
        Assert.Contains("자식 프로세스는 함께 종료되지 않습니다", h.Dialogs.LastConfirmMessage);
        Assert.Contains("2개 작업에 중지를 요청했습니다", h.Vm.ActionResultText);
        h.Vm.Dispose();
    }

    [Fact]
    public void CancelAll_WhenDeclined_SignalsNothing()
    {
        var h = Build();
        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);
        h.Vm.Attach();
        h.Dialogs.ConfirmAnswer = false;

        h.Vm.CancelAllCommand.Execute(null);

        Assert.False(turn.Token.IsCancellationRequested);
        h.Vm.Dispose();
    }

    [Fact]
    public void SweepOrphans_IsDisabledWithoutCandidates()
    {
        var h = Build();
        h.Vm.Attach();

        Assert.False(h.Vm.SweepOrphansCommand.CanExecute(null));

        // 소유 도구가 끝난 뒤에도 남는 프로세스(start_process)만 고아 후보가 된다.
        h.Probe.Add(910, "notepad", T0);
        h.Registry.TrackDetachedProcess(Pid(910, "notepad"), null);
        h.Vm.RefreshCommand.Execute(null);

        Assert.Equal(1, h.Vm.OrphanCandidateCount);
        Assert.True(h.Vm.SweepOrphansCommand.CanExecute(null));

        h.Vm.SweepOrphansCommand.Execute(null);

        Assert.False(h.Probe.IsAlive(910));
        Assert.Contains("강제 종료 1개", h.Vm.ActionResultText);
        h.Vm.Dispose();
    }

    [Fact]
    public void SweepOrphans_ConfirmationSaysOnlyOurProcesses()
    {
        var h = Build();
        h.Probe.Add(911, "notepad", T0);
        h.Registry.TrackDetachedProcess(Pid(911, "notepad"), null);
        h.Vm.Attach();

        h.Vm.SweepOrphansCommand.Execute(null);

        // 시스템 프로세스를 훑지 않는다는 사실을 확인 문구에 남겨야 오해가 없다.
        Assert.Contains("이 앱이 띄운 프로세스만", h.Dialogs.LastConfirmMessage);
        h.Vm.Dispose();
    }

    // ── /loop 행 ─────────────────────────────────────────────────────────

    [Fact]
    public void LoopRow_ReadsTheControllerDirectly_AndReusesTheSharedFormatter()
    {
        var h = Build();
        h.Loop.IsRunning = true;
        h.Loop.Status = LoopStatusSnapshot.Idle with
        {
            State = LoopState.Waiting, Iteration = 3, MaxIterations = 25, Remaining = TimeSpan.FromSeconds(74),
        };

        h.Vm.Attach();

        // 문구는 GUI·CLI 공용 포매터를 재사용한다(갈리면 그것 자체가 버그 리포트가 된다).
        Assert.Equal(LoopStatusFormatter.Describe(h.Loop.Status), h.Vm.LoopStatusText);
        Assert.Contains("반복 3/25", h.Vm.LoopStatusText);
        Assert.True(h.Vm.StopLoopCommand.CanExecute(null));
        h.Vm.Dispose();
    }

    [Fact]
    public void StopLoop_UsesUserStoppedReason_AndIsDisabledWhenIdle()
    {
        var h = Build();
        h.Vm.Attach();
        Assert.False(h.Vm.StopLoopCommand.CanExecute(null));

        h.Loop.IsRunning = true;
        h.Vm.RefreshCommand.Execute(null);
        Assert.True(h.Vm.StopLoopCommand.CanExecute(null));

        h.Vm.StopLoopCommand.Execute(null);

        Assert.Equal(LoopStopReason.UserStopped, h.Loop.LastStopReason);
        h.Vm.Dispose();
    }

    [Fact]
    public void LoopChanged_RefreshesTheRow()
    {
        var h = Build();
        h.Vm.Attach();
        h.Loop.Status = LoopStatusSnapshot.Idle with { State = LoopState.RunningTurn, Iteration = 1, MaxIterations = 5 };

        h.Loop.Raise(new LoopStarted(h.Loop.Status));

        Assert.Contains("반복 1/5", h.Vm.LoopStatusText);
        h.Vm.Dispose();
    }

    // ── 완료 이력 ────────────────────────────────────────────────────────

    [Fact]
    public void Recent_ShowsThatTheStopActuallyLanded()
    {
        var h = Build();
        var tool = h.Registry.BeginTool(null, CancellationToken.None, "run_command", ToolRisk.Execute, NoArgs);
        h.Vm.Attach();

        h.Vm.CancelItemCommand.Execute(h.Vm.Items.Single());
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        tool.Dispose();   // 도구가 실제로 접혔다

        h.Vm.RefreshCommand.Execute(null);

        // 진행 중 목록에서 사라지는 것만으로는 "중지가 먹었는지" 알 수 없다 — 이력이 그 증거다.
        Assert.Empty(h.Vm.Items);
        var entry = Assert.Single(h.Vm.Recent);
        Assert.Equal("run_command", entry.Title);
        Assert.Equal("중지됨", entry.StateText);
        Assert.True(h.Vm.HasRecent);
        h.Vm.Dispose();
    }

    // ── 수명 (누수) ──────────────────────────────────────────────────────

    [Fact]
    public void Detach_StopsListening_SoClosedWindowsDoNotKeepPolling()
    {
        var h = Build();
        h.Vm.Attach();
        h.Vm.Detach();

        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);
        h.Loop.Raise(new LoopStarted(LoopStatusSnapshot.Idle));

        // 등기소·루프는 App 수명 싱글턴이다 — 창마다 구독이 쌓이면 닫힌 창이 계속 관측을 돌린다.
        Assert.Empty(h.Vm.Items);
        h.Vm.Dispose();
    }

    [Fact]
    public void Attach_IsIdempotent_SoReopeningDoesNotStackSubscriptions()
    {
        var h = Build();
        var refreshes = 0;
        h.Vm.Items.CollectionChanged += (_, _) => refreshes++;

        h.Vm.Attach();
        h.Vm.Attach();

        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);

        Assert.Equal(1, refreshes);   // 구독이 두 겹이면 한 번의 변화에 두 번 반응한다
        h.Vm.Dispose();
    }

    [Fact]
    public void Dispose_DetachesEverything()
    {
        var h = Build();
        h.Vm.Attach();
        h.Vm.Dispose();

        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);

        Assert.Empty(h.Vm.Items);
    }

    [Fact]
    public void Close_AsksTheViewToClose_WithoutTouchingRunningWork()
    {
        var h = Build();
        using var turn = h.Registry.BeginTurn(CancellationToken.None, 25);
        h.Vm.Attach();
        var asked = 0;
        h.Vm.CloseRequested += (_, _) => asked++;

        h.Vm.CloseCommand.Execute(null);

        Assert.Equal(1, asked);
        Assert.False(turn.Token.IsCancellationRequested);   // 창을 닫는 것은 중지가 아니다
        h.Vm.Dispose();
    }
}
