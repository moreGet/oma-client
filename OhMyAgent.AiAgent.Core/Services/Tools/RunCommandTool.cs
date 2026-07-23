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
        {"type":"object","properties":{"shell":{"type":"string","enum":["powershell","cmd","bash"]},"command":{"type":"string"}},"required":["shell","command"]}
        """);

    public string Name => "run_command";
    public string Description => OperatingSystem.IsWindows()
        ? "Execute a PowerShell or CMD command in the workspace directory."
        : "Execute a bash command in the workspace directory.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Execute;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var shell = ToolSchemas.GetString(args, "shell");
        var command = ToolSchemas.GetString(args, "command");

        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("command 가 비어 있습니다.");

        var type = shell?.ToLowerInvariant() switch
        {
            "cmd"  => ScriptType.Cmd,
            "bash" => ScriptType.Bash,
            _      => ScriptType.PowerShell,
        };

        // Windows 에는 bash 가 없다 — 폴백하지 않고 명확히 실패시켜 모델의 셸 오인을 차단한다.
        // (Windows 정상 경로인 powershell/cmd 는 완전 불변.)
        if (type == ScriptType.Bash && OperatingSystem.IsWindows())
            return ToolResult.Fail("bash 는 이 호스트(Windows)에서 지원되지 않습니다. powershell 또는 cmd 를 쓰세요.");

        // 보안 검증은 "실제로 실행될 셸"의 규칙으로 해야 한다.
        // Linux 에서는 ScriptExecutor 가 셸 파라미터와 무관하게 모든 타입을 /bin/bash 로 귀결시키므로,
        // 모델이 shell="cmd"/"powershell" 로 보내 bash 블랙리스트(rm -rf, dd, sudo, /etc …)를 우회하는 것을 막기 위해
        // 비-Windows 에서는 항상 Bash 규칙으로 검증한다. (Windows 는 요청 셸 그대로 — 기존 동작 불변.)
        var validationType = OperatingSystem.IsWindows() ? type : ScriptType.Bash;
        var validation = SecurityValidator.Validate(command, validationType);
        if (!validation.IsValid)
            return ToolResult.Fail($"보안 검증 실패: {validation.Reason}");

        try
        {
            var workingDir = ctx.Workspace.Root;
            // Linux 에서는 세 타입 모두 ScriptExecutor 내부에서 /bin/bash 로 귀결된다.
            var result = type == ScriptType.Cmd
                ? await executor.ExecuteCmdAsync(command, workingDirectory: workingDir, ct: ct).ConfigureAwait(false)
                : await executor.ExecutePowerShellAsync(command, workingDirectory: workingDir, ct: ct).ConfigureAwait(false);

            var payload = new
            {
                exit_code = result.ExitCode,
                stdout = Cap(result.Stdout),
                stderr = Cap(result.Stderr),
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

    // 모델 컨텍스트 보호 — 스트림당 상한. 초과 시 뒤를 자르고 고지(전체 로그는 셸에서 파일 리다이렉트 권장).
    private const int MaxStreamChars = 24_000;
    private static string? Cap(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= MaxStreamChars) return s;
        return s[..MaxStreamChars] + $"\n\n[... 출력이 {MaxStreamChars}자로 잘렸습니다. 필요하면 명령을 좁히거나 파일로 리다이렉트하세요 ...]";
    }
}
