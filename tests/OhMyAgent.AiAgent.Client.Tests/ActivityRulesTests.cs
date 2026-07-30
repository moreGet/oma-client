using System;
using System.Text.Json;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 태스크 매니저의 순수 판정 로직. 여기 있는 규칙이 곧 "남의 프로세스를 죽이지 않는다"는 보증이므로
/// 실제 프로세스를 하나도 띄우지 않고 전수 검증한다.
/// </summary>
public class ProcessIdentityRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static TrackedProcessIdentity Recorded(
        int pid = 1234, string name = "cmd", DateTimeOffset? started = null)
        => new(pid, name, started ?? T0);

    [Fact]
    public void ExactMatch_IsAlive()
    {
        var verdict = ProcessIdentityRules.Verify(Recorded(), new ProcessObservation(1234, "cmd", T0));
        Assert.Equal(TrackedProcessVerdict.Alive, verdict);
    }

    [Fact]
    public void SmallStartTimeDrift_IsStillAlive()
    {
        // Process.StartTime 은 조회 경로에 따라 밀리초가 미세하게 흔들린다 — 자기 프로세스를 재사용으로 오판하면
        // 정리 기능이 아무 것도 못 하게 된다.
        var verdict = ProcessIdentityRules.Verify(
            Recorded(), new ProcessObservation(1234, "cmd", T0.AddMilliseconds(300)));
        Assert.Equal(TrackedProcessVerdict.Alive, verdict);
    }

    [Fact]
    public void LargeStartTimeDrift_IsRecycled()
    {
        // 같은 PID 인데 시작 시각이 다르다 = OS 가 PID 를 재사용했다 = 남의 프로세스다.
        var verdict = ProcessIdentityRules.Verify(
            Recorded(), new ProcessObservation(1234, "cmd", T0.AddMinutes(5)));
        Assert.Equal(TrackedProcessVerdict.Recycled, verdict);
    }

    [Fact]
    public void DifferentName_IsRecycled()
    {
        var verdict = ProcessIdentityRules.Verify(
            Recorded(name: "cmd"), new ProcessObservation(1234, "notepad", T0));
        Assert.Equal(TrackedProcessVerdict.Recycled, verdict);
    }

    [Fact]
    public void NameComparison_IgnoresCase()
    {
        var verdict = ProcessIdentityRules.Verify(
            Recorded(name: "CMD"), new ProcessObservation(1234, "cmd", T0));
        Assert.Equal(TrackedProcessVerdict.Alive, verdict);
    }

    [Fact]
    public void NoObservation_MeansExited()
    {
        Assert.Equal(TrackedProcessVerdict.Exited, ProcessIdentityRules.Verify(Recorded(), null));
    }

    [Fact]
    public void MissingRecordedStartTime_IsUnverifiable()
    {
        // 권한 부족으로 시작 시각을 못 읽은 경우 — 확신이 없으면 죽이지 않는다.
        var verdict = ProcessIdentityRules.Verify(
            new TrackedProcessIdentity(1234, "cmd", null), new ProcessObservation(1234, "cmd", T0));
        Assert.Equal(TrackedProcessVerdict.Unverifiable, verdict);
    }

    [Fact]
    public void MissingObservedStartTime_IsUnverifiable()
    {
        var verdict = ProcessIdentityRules.Verify(Recorded(), new ProcessObservation(1234, "cmd", null));
        Assert.Equal(TrackedProcessVerdict.Unverifiable, verdict);
    }

    [Fact]
    public void OnlyAlive_MayBeKilled()
    {
        Assert.True(ProcessIdentityRules.MayKill(TrackedProcessVerdict.Alive));
        Assert.False(ProcessIdentityRules.MayKill(TrackedProcessVerdict.Exited));
        Assert.False(ProcessIdentityRules.MayKill(TrackedProcessVerdict.Recycled));
        Assert.False(ProcessIdentityRules.MayKill(TrackedProcessVerdict.Unverifiable));
    }
}

public class OrphanRulesTests
{
    [Fact]
    public void DeadOwner_AliveProcess_IsOrphan()
    {
        Assert.True(OrphanRules.IsOrphan(
            AgentActivityKind.ChildProcess, ownerAlive: false, TrackedProcessVerdict.Alive));
    }

    [Fact]
    public void LiveOwner_IsNotOrphan()
    {
        // run_command 가 도는 중인 프로세스를 "고아"로 잡으면 실행 중인 명령을 죽인다.
        Assert.False(OrphanRules.IsOrphan(
            AgentActivityKind.ChildProcess, ownerAlive: true, TrackedProcessVerdict.Alive));
    }

    [Theory]
    [InlineData(TrackedProcessVerdict.Recycled)]
    [InlineData(TrackedProcessVerdict.Unverifiable)]
    [InlineData(TrackedProcessVerdict.Exited)]
    public void NonAliveVerdict_IsNeverOrphan(TrackedProcessVerdict verdict)
    {
        // 재사용/확인불가/이미종료는 정리 대상이 아니다 — 특히 Recycled 를 정리하면 남의 프로세스를 죽인다.
        Assert.False(OrphanRules.IsOrphan(AgentActivityKind.ChildProcess, ownerAlive: false, verdict));
    }

    [Theory]
    [InlineData(AgentActivityKind.Tool)]
    [InlineData(AgentActivityKind.Turn)]
    public void NonProcessKinds_AreNeverOrphans(AgentActivityKind kind)
    {
        Assert.False(OrphanRules.IsOrphan(kind, ownerAlive: false, TrackedProcessVerdict.Alive));
    }
}

public class ActivityHealthRulesTests
{
    [Fact]
    public void FreshTool_IsNormal()
    {
        Assert.Equal(AgentActivityHealth.Normal,
            ActivityHealthRules.Classify(AgentActivityKind.Tool, TimeSpan.FromSeconds(1), null));
    }

    [Fact]
    public void SlowTool_IsLongRunning()
    {
        Assert.Equal(AgentActivityHealth.LongRunning,
            ActivityHealthRules.Classify(AgentActivityKind.Tool, ActivityHealthRules.ToolLongRunning, null));
    }

    [Fact]
    public void TurnThreshold_IsLooserThanTool()
    {
        // 턴은 모델 왕복 + 도구 여러 개라 도구 임계값을 넘겨도 아직 정상이다.
        Assert.Equal(AgentActivityHealth.Normal,
            ActivityHealthRules.Classify(AgentActivityKind.Turn, ActivityHealthRules.ToolLongRunning, null));
        Assert.Equal(AgentActivityHealth.LongRunning,
            ActivityHealthRules.Classify(AgentActivityKind.Turn, ActivityHealthRules.TurnLongRunning, null));
    }

    [Fact]
    public void ProcessThreshold_IsLoosest()
    {
        Assert.Equal(AgentActivityHealth.Normal,
            ActivityHealthRules.Classify(AgentActivityKind.ChildProcess, ActivityHealthRules.TurnLongRunning, null));
        Assert.Equal(AgentActivityHealth.LongRunning,
            ActivityHealthRules.Classify(AgentActivityKind.ChildProcess, ActivityHealthRules.ProcessLongRunning, null));
    }

    [Fact]
    public void JustCancelled_IsPending()
    {
        Assert.Equal(AgentActivityHealth.CancelPending,
            ActivityHealthRules.Classify(AgentActivityKind.Tool, TimeSpan.FromSeconds(2), TimeSpan.Zero));
    }

    [Fact]
    public void CancelPastGrace_IsStalled()
    {
        // 이것이 "중지를 눌렀는데 안 멈추는 항목"이다 — 사용자에게 자식 프로세스 강제 종료를 안내할 근거.
        Assert.Equal(AgentActivityHealth.CancelStalled,
            ActivityHealthRules.Classify(
                AgentActivityKind.Tool, TimeSpan.FromSeconds(6), ActivityHealthRules.CancelGrace));
    }

    [Fact]
    public void CancelState_WinsOverElapsed()
    {
        // 취소를 눌렀는데 "오래 걸림"만 보이면 사용자는 자기 클릭이 접수됐는지 알 수 없다.
        Assert.Equal(AgentActivityHealth.CancelPending,
            ActivityHealthRules.Classify(
                AgentActivityKind.Tool, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1)));
    }
}

public class ActivityLabelTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void PrefersCommand_ForRunCommand()
    {
        Assert.Equal("dir C:\\",
            ActivityLabel.Summarize(Json("""{"shell":"cmd","command":"dir C:\\"}""")));
    }

    [Fact]
    public void PrefersPath_ForFileTools()
    {
        Assert.Equal("src/App.cs", ActivityLabel.Summarize(Json("""{"path":"src/App.cs"}""")));
    }

    [Fact]
    public void PrefersDescription_ForSubagentTask()
    {
        // 병렬 task 여러 개가 이름만 같으면 무엇을 중지하는지 알 수 없다.
        Assert.Equal("로그 분석",
            ActivityLabel.Summarize(Json("""{"description":"로그 분석","prompt":"아주 긴 프롬프트"}""")));
    }

    [Fact]
    public void FlattensNewlines()
    {
        Assert.Equal("echo a echo b", ActivityLabel.Summarize(Json("""{"command":"echo a\necho b"}""")));
    }

    [Fact]
    public void TruncatesLongValue()
    {
        var summary = ActivityLabel.Summarize(Json($"{{\"command\":\"{new string('x', 500)}\"}}"));
        Assert.NotNull(summary);
        Assert.Equal(ActivityLabel.MaxDetailChars + 1, summary!.Length);   // 상한 + 말줄임표 1자
        Assert.EndsWith("…", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipsHugeFallbackValues()
    {
        // write_file 의 본문 전체가 UI 로 흘러들면 안 된다. 우선순위 키가 없으면 긴 값은 건너뛴다.
        Assert.Null(ActivityLabel.Summarize(Json($"{{\"content\":\"{new string('y', 300)}\"}}")));
    }

    [Fact]
    public void UsesShortFallbackValue()
    {
        Assert.Equal("utf-8", ActivityLabel.Summarize(Json("""{"encoding":"utf-8"}""")));
    }

    [Fact]
    public void NonObject_HasNoSummary()
    {
        Assert.Null(ActivityLabel.Summarize(Json("[1,2,3]")));
        Assert.Null(ActivityLabel.Summarize(Json("{}")));
    }
}
