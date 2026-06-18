using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class RunCommandTool(IScriptExecutor executor) : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"shell":{"type":"string","enum":["powershell","cmd"]},"command":{"type":"string"}},"required":["shell","command"]}
        """);

    public string Name => "run_command";
    public string Description => "Execute a PowerShell or CMD command in the workspace directory.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Execute;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var shell = ToolSchemas.GetString(args, "shell");
        var command = ToolSchemas.GetString(args, "command");

        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("command 가 비어 있습니다.");

        var type = string.Equals(shell, "cmd", StringComparison.OrdinalIgnoreCase)
            ? ScriptType.Cmd
            : ScriptType.PowerShell;

        var validation = SecurityValidator.Validate(command, type);
        if (!validation.IsValid)
            return ToolResult.Fail($"보안 검증 실패: {validation.Reason}");

        try
        {
            var workingDir = ctx.Workspace.Root;
            var result = type == ScriptType.Cmd
                ? await executor.ExecuteCmdAsync(command, workingDirectory: workingDir, ct: ct).ConfigureAwait(false)
                : await executor.ExecutePowerShellAsync(command, workingDirectory: workingDir, ct: ct).ConfigureAwait(false);

            var payload = new
            {
                exit_code = result.ExitCode,
                stdout = result.Stdout,
                stderr = result.Stderr,
                timed_out = result.TimedOut
            };

            var isError = result.ExitCode != 0 || result.TimedOut;
            return new ToolResult(JsonSerializer.Serialize(payload, AgentJson.Options), isError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}
