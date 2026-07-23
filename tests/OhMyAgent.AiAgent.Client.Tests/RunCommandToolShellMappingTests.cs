using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Mcp;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// RunCommandTool 의 shell 문자열 → ScriptExecutor 메서드 매핑 검증.
/// (테스트 러너는 net10.0-windows 이므로 OperatingSystem.IsWindows() == true.)
/// </summary>
public sealed class RunCommandToolShellMappingTests
{
    private sealed class RecordingExecutor : IScriptExecutor
    {
        public string? LastMethod;
        public string? LastScript;

        public Task<ScriptResult> ExecutePowerShellAsync(string script, int timeoutMs = 30000, string? workingDirectory = null, CancellationToken ct = default)
        {
            LastMethod = "powershell"; LastScript = script;
            return Task.FromResult(new ScriptResult { ExitCode = 0, Stdout = "ok" });
        }

        public Task<ScriptResult> ExecuteCmdAsync(string command, int timeoutMs = 30000, string? workingDirectory = null, CancellationToken ct = default)
        {
            LastMethod = "cmd"; LastScript = command;
            return Task.FromResult(new ScriptResult { ExitCode = 0, Stdout = "ok" });
        }
    }

    private sealed class FakeWorkspace : IWorkspaceContext
    {
        public string Root => System.IO.Path.GetTempPath();
        public System.Collections.Generic.IReadOnlyList<string> Roots => new[] { Root };
        public string ResolvePath(string relativeOrAbsolute) => relativeOrAbsolute;
        public bool IsInsideWorkspace(string path) => true;
        public void SetRoot(string root) { }
        public void SetRoots(System.Collections.Generic.IReadOnlyList<string> roots) { }
    }

    private static JsonElement Args(string shell, string command)
        => JsonDocument.Parse($$"""{"shell":"{{shell}}","command":"{{command}}"}""").RootElement;

    private static ToolContext Ctx() => new(new FakeWorkspace(), PermissionMode.Manual);

    [Fact]
    public async Task Cmd_RoutesToExecuteCmd()
    {
        var exec = new RecordingExecutor();
        var tool = new RunCommandTool(exec);

        await tool.ExecuteAsync(Args("cmd", "echo hi"), Ctx());

        Assert.Equal("cmd", exec.LastMethod);
    }

    [Fact]
    public async Task PowerShell_RoutesToExecutePowerShell()
    {
        var exec = new RecordingExecutor();
        var tool = new RunCommandTool(exec);

        await tool.ExecuteAsync(Args("powershell", "echo hi"), Ctx());

        Assert.Equal("powershell", exec.LastMethod);
    }

    [Fact]
    public async Task UnknownShell_DefaultsToPowerShell()
    {
        var exec = new RecordingExecutor();
        var tool = new RunCommandTool(exec);

        await tool.ExecuteAsync(Args("zsh", "echo hi"), Ctx());

        Assert.Equal("powershell", exec.LastMethod);
    }

    [Fact]
    public async Task Bash_OnWindows_FailsWithoutInvokingExecutor()
    {
        var exec = new RecordingExecutor();
        var tool = new RunCommandTool(exec);

        var result = await tool.ExecuteAsync(Args("bash", "echo hi"), Ctx());

        Assert.True(result.IsError);
        Assert.Contains("bash", result.Content);
        Assert.Null(exec.LastMethod);   // 실행기 미호출(조기 차단)
    }
}
