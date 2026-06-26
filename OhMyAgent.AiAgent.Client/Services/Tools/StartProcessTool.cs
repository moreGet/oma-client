using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class StartProcessTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"arguments":{"type":"string"},"working_directory":{"type":"string"}},"required":["path"]}
        """);

    public string Name => "start_process";
    public string Description => "Start a new process (executable or file). Returns the started PID. 'working_directory' defaults to the workspace root. Absolute 'path' is allowed but launches arbitrary executables — use with care.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Execute;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Fail("path 가 비어 있습니다."));

        var arguments = ToolSchemas.GetString(args, "arguments");
        var workingDirArg = ToolSchemas.GetString(args, "working_directory");

        string workingDir;
        if (string.IsNullOrWhiteSpace(workingDirArg))
        {
            workingDir = ctx.Workspace.Root;
        }
        else
        {
            try
            {
                workingDir = ctx.Workspace.ResolvePath(workingDirArg);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail($"working_directory 가 작업 디렉토리를 벗어납니다: {ex.Message}"));
            }
            if (!Directory.Exists(workingDir))
                return Task.FromResult(ToolResult.Fail($"working_directory 가 존재하지 않습니다: {workingDirArg}"));
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc is null)
                return Task.FromResult(ToolResult.Fail($"프로세스를 시작하지 못했습니다: {path}"));

            return Task.FromResult(ToolResult.Json(new { started = true, pid = proc.Id, path }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"프로세스 시작 실패: {ex.Message}"));
        }
    }
}
