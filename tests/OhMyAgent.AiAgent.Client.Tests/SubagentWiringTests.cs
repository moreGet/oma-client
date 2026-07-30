using System;
using System.Linq;
using System.Net.Http;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Loop;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// App.StartupAsync 의 서브에이전트 배선이 실제 도구 목록에 대해 안전한 집합을 만드는지 검증한다.
/// TaskToolTests 가 허용목록의 "내용"을 지킨다면, 여기서는 그 목록이 실제 도구들과 맞물려
/// 위험한 도구를 하나도 통과시키지 않는지를 확인한다.
/// </summary>
public class SubagentWiringTests
{
    /// <summary>App.xaml.cs tools[] 와 같은 구성(외부 자원이 필요 없는 것만 실제 인스턴스화).</summary>
    private static ITool[] RealTools() =>
    [
        new RunCommandTool(new ScriptExecutor()),
        new ReadFileTool(),
        new WriteFileTool(),
        new EditFileTool(),
        new ListDirectoryTool(),
        new GlobTool(),
        new GrepTool(),
        new CreateDirectoryTool(),
        new MoveTool(),
        new CopyTool(),
        new DeleteTool(),
        new GetEnvironmentTool(),
        new ClipboardReadTool(),
        new ClipboardWriteTool(),
        new ListProcessesTool(),
        new ListProcessesMemoryKbTool(),
        new StartProcessTool(),
        new KillProcessTool(),
        new HttpFetchTool(new HttpClient()),
        new ScreenshotTool(),
        new ManageTodosTool(new TodoService()),
        new ScheduleWakeupTool(new WakeupSink()),
        new ReadCsvTool(),
        new WriteCsvTool(),
        new ReadExcelTool(),
        new WriteExcelTool(),
        new ReadPdfTool(),
        new ReadDocumentTool(),
        new ReadPptxTool(),
        new WritePptxTool(),
        new ReadHwpxTool(),
        new CompressFilesTool(),
        new ExtractArchiveTool(),
        // 서버를 호출하지 않는다 — 목록 구성에만 쓰이는 인스턴스다.
        new GenerateImageTool(new AgentApiClient(new HttpClient(), new FakeSettingsService())),
    ];

    /// <summary>App.StartupAsync 의 필터와 동일.</summary>
    private static ITool[] SubagentTools()
        => RealTools().Where(t => TaskTool.AllowedToolNames.Contains(t.Name)).ToArray();

    [Fact]
    public void SubagentGetsNoToolThatCanChangeAnything()
    {
        var risky = SubagentTools().Where(t => t.Risk != ToolRisk.ReadOnly).Select(t => t.Name).ToList();

        Assert.Empty(risky);
    }

    [Fact]
    public void SubagentGetsUsefulInvestigationTools()
    {
        var names = SubagentTools().Select(t => t.Name).ToList();

        // 조사를 못 할 만큼 좁으면 도구 자체가 무의미해진다.
        Assert.Contains("read_file", names);
        Assert.Contains("glob", names);
        Assert.Contains("grep", names);
        Assert.True(names.Count >= 8, $"서브에이전트 도구가 너무 적습니다: {names.Count}개");
    }

    [Fact]
    public void SubagentCannotSpawnSubagent()
    {
        // 메인 레지스트리엔 task 가 있지만, 서브 레지스트리엔 없어야 한다(무한 재귀 방지).
        Assert.DoesNotContain("task", SubagentTools().Select(t => t.Name));
    }

    [Fact]
    public void SubagentCannotTouchParentTodoList()
    {
        // ManageTodosTool 은 ReadOnly 라 Risk 필터로는 걸러지지 않는다 — 허용목록이 유일한 방어선이다.
        Assert.DoesNotContain("manage_todos", SubagentTools().Select(t => t.Name));
    }

    [Fact]
    public void SubagentCannotHijackParentLoopPacing()
    {
        // ScheduleWakeupTool 도 ReadOnly 라 Risk 필터를 그냥 통과한다 — manage_todos 와 같은 구멍이다.
        // 서브에이전트가 부모의 /loop 다음 실행 시점을 정해버리면, 사용자가 시작한 루프의 페이싱이
        // 위임 작업에 납치된다. 허용목록이 유일한 방어선이므로 여기서 잠근다.
        Assert.DoesNotContain("schedule_wakeup", SubagentTools().Select(t => t.Name));
    }

    [Fact]
    public void SubagentCannotBurnImageQuota()
    {
        // generate_image 는 Risk=Write 라 Risk 필터로도 걸리지만, 허용목록이 유일한 계약이므로 여기서 못 박는다.
        // 서브에이전트는 사용자가 직접 지시하지 않은 하위 루프다 — 그 루프가 유료 이미지 생성을 반복 호출하면
        // 사용자가 승인한 적 없는 쿼터 소모가 일어나고, ReadOnly 도구만 도는 조사 임무에 필요하지도 않다.
        Assert.DoesNotContain("generate_image", SubagentTools().Select(t => t.Name));
    }

    [Fact]
    public void AllowlistHasNoStaleEntries()
    {
        // 허용목록에 있는데 실제로 존재하지 않는 도구명 = 오타이거나 삭제된 도구.
        var real = RealTools().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = TaskTool.AllowedToolNames.Where(n => !real.Contains(n)).ToList();

        Assert.Empty(stale);
    }

    [Fact]
    public void MainRegistryExposesTaskTool()
    {
        var subOrch = new FakeOrchestrator();
        var registry = new ToolRegistry([.. RealTools(), new TaskTool(subOrch, new WorkspaceContext(new FakeSettingsService()))]);

        Assert.True(registry.TryGet("task", out var task));
        Assert.Equal(ToolRisk.ReadOnly, task!.Risk);
    }
}
