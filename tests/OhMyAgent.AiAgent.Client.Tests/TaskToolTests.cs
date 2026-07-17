using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 서브에이전트. 핵심 계약은 셋이다:
///  (1) 부모 컨텍스트를 오염시키지 않는다 — 최종 텍스트만 돌려준다(격리가 이 도구의 존재 이유).
///  (2) 부모/사용자 상태를 건드릴 수 없다 — 허용목록 밖 도구는 보이지 않는다.
///  (3) 실패를 숨기지 않는다 — 빈 결과를 "조사했는데 없음"으로 위장하지 않는다.
/// </summary>
public class TaskToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext Ctx()
    {
        var ws = new WorkspaceContext(new FakeSettingsService());
        return new ToolContext(ws, PermissionMode.FullAuto);
    }

    private static TaskTool Make(params AgentEvent[] events)
        => new(new FakeOrchestrator(events), Ctx().Workspace);

    // ── 허용목록: Risk 로 거르면 안 되는 이유를 고정한다 ──

    [Fact]
    public void AllowedTools_ExcludeManageTodos()
    {
        // manage_todos 는 ReadOnly 지만 TodoService 싱글톤으로 부모의 할 일 목록을 덮어쓴다.
        // Risk 기반 필터로 회귀하면 이 테스트가 잡는다.
        Assert.DoesNotContain("manage_todos", TaskTool.AllowedToolNames);
    }

    [Fact]
    public void AllowedTools_ExcludeTaskItself()
    {
        // 자기 자신이 들어가면 서브에이전트가 서브에이전트를 무한 재귀로 띄운다.
        Assert.DoesNotContain("task", TaskTool.AllowedToolNames);
    }

    [Fact]
    public void AllowedTools_ExcludeUserSurveillance()
    {
        // 조사에 불필요한데 사용자 화면·클립보드를 엿본다.
        Assert.DoesNotContain("clipboard_read", TaskTool.AllowedToolNames);
        Assert.DoesNotContain("screenshot", TaskTool.AllowedToolNames);
    }

    [Fact]
    public void AllowedTools_ExcludeEveryMutatingTool()
    {
        string[] mutating =
        [
            "write_file", "edit_file", "delete", "move", "copy", "create_directory",
            "run_command", "start_process", "kill_process", "http_fetch",
            "write_csv", "write_excel", "write_pptx", "compress_files", "extract_archive",
        ];

        foreach (var tool in mutating)
            Assert.DoesNotContain(tool, TaskTool.AllowedToolNames);
    }

    [Fact]
    public void AllowedTools_IncludeInvestigationEssentials()
    {
        foreach (var t in new[] { "read_file", "glob", "grep", "list_directory" })
            Assert.Contains(t, TaskTool.AllowedToolNames);
    }

    [Fact]
    public void AllowedTools_AreCaseInsensitive()
    {
        Assert.Contains("READ_FILE", TaskTool.AllowedToolNames);
    }

    [Fact]
    public void Risk_IsReadOnlySoNoApprovalStorm()
    {
        // ReadOnly 여야 승인 게이트를 타지 않는다(gated = mode != FullAuto && risk != ReadOnly).
        Assert.Equal(ToolRisk.ReadOnly, Make().Risk);
    }

    // ── 결과 전달 ──

    [Fact]
    public async Task ReturnsOnlyFinalText()
    {
        var tool = Make(
            new AgentTextDelta("이건 부모가 보면 안 됨"),
            new AgentToolCallStarted("c1", "read_file", Args("{}"), ToolRisk.ReadOnly),
            new AgentDone("결론: MainWindow.xaml.cs:42 에 있습니다", null));

        var result = await tool.ExecuteAsync(
            Args("""{"description":"찾기","prompt":"X 를 찾아라"}"""), Ctx());

        Assert.False(result.IsError);
        Assert.Equal("결론: MainWindow.xaml.cs:42 에 있습니다", result.Content);
        // 중간 이벤트가 새어나오면 컨텍스트 격리라는 목적이 무너진다.
        Assert.DoesNotContain("부모가 보면 안 됨", result.Content);
    }

    [Fact]
    public async Task IsolatesSession_ParentHistoryNotPassed()
    {
        var fake = new FakeOrchestrator(new AgentDone("ok", null));
        var tool = new TaskTool(fake, Ctx().Workspace);

        await tool.ExecuteAsync(Args("""{"description":"d","prompt":"임무"}"""), Ctx());

        // 서브에이전트 세션은 자체 시스템 프롬프트로 시작해야 한다(부모 이력 없음).
        Assert.NotNull(fake.LastSession);
        Assert.Single(fake.LastSession!.Messages);   // RunAsync 진입 시점엔 시스템 프롬프트 1개뿐
        Assert.Equal(MessageRole.System, fake.LastSession.Messages[0].Role);
        Assert.Contains("read-only investigation subagent", fake.LastSession.Messages[0].Content!);
    }

    [Fact]
    public async Task UsesLowerIterationBudgetThanParent()
    {
        var fake = new FakeOrchestrator(new AgentDone("ok", null));
        await new TaskTool(fake, Ctx().Workspace)
            .ExecuteAsync(Args("""{"description":"d","prompt":"임무"}"""), Ctx());

        // 헤매는 하위 루프가 사용자 대기시간·토큰을 잠식하지 않도록 메인(기본 25)보다 낮아야 한다.
        Assert.NotNull(fake.LastMaxIterations);
        Assert.True(fake.LastMaxIterations < 25);
    }

    // ── 실패를 숨기지 않는다 ──

    [Fact]
    public async Task EmptyResult_IsFailureNotSilentSuccess()
    {
        // 빈 결과를 성공으로 돌려주면 부모가 "조사했는데 아무것도 없다"로 오해한다.
        var result = await Make(new AgentDone("", null))
            .ExecuteAsync(Args("""{"description":"d","prompt":"임무"}"""), Ctx());

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ErrorWithNoText_IsReportedAsFailure()
    {
        var result = await Make(new AgentError("max_iterations", "최대 반복 도달"))
            .ExecuteAsync(Args("""{"description":"d","prompt":"임무"}"""), Ctx());

        Assert.True(result.IsError);
        Assert.Contains("max_iterations", result.Content);
    }

    [Fact]
    public async Task PartialResult_CarriesWarningSoParentCanJudge()
    {
        // 결과는 있으나 완주 못 한 경우 — 결과를 주되 불완전함을 반드시 알린다.
        var result = await Make(
                new AgentError("max_iterations", "최대 반복 도달"),
                new AgentDone("찾은 것: A, B", null))
            .ExecuteAsync(Args("""{"description":"d","prompt":"임무"}"""), Ctx());

        Assert.False(result.IsError);
        Assert.Contains("찾은 것: A, B", result.Content);
        Assert.Contains("불완전", result.Content);
    }

    [Fact]
    public async Task EmptyPrompt_IsRejected()
    {
        var result = await Make(new AgentDone("x", null))
            .ExecuteAsync(Args("""{"description":"d","prompt":"  "}"""), Ctx());

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TaskTool(new FakeOrchestrator(throwOnCancel: true), Ctx().Workspace)
                .ExecuteAsync(Args("""{"description":"d","prompt":"임무"}"""), Ctx(), cts.Token));
    }
}

/// <summary>지정한 이벤트를 순서대로 방출하는 오케스트레이터 스텁.</summary>
internal sealed class FakeOrchestrator(params AgentEvent[] events) : IAgentOrchestrator
{
    private readonly bool _throwOnCancel;

    public AgentSession? LastSession { get; private set; }
    public int? LastMaxIterations { get; private set; }

    public FakeOrchestrator(bool throwOnCancel) : this([]) => _throwOnCancel = throwOnCancel;

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string userGoal,
        AgentSession session,
        IReadOnlyList<Attachment>? attachments = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        int? maxIterations = null)
    {
        // RunAsync 진입 시점의 세션 상태를 기록한다(사용자 메시지가 추가되기 전).
        LastSession = new AgentSession(session.Id, session.Messages);
        LastMaxIterations = maxIterations;

        if (_throwOnCancel)
            ct.ThrowIfCancellationRequested();

        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }

        await Task.CompletedTask;
    }
}
