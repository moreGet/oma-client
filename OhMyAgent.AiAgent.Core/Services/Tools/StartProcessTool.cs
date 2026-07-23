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
    public string Description => "Start a new process from an executable or file inside the workspace. Returns the started PID. 'path' must resolve inside the workspace; 'working_directory' defaults to the workspace root.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Execute;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(ToolResult.Fail("path 가 비어 있습니다."));

        // path 는 샌드박스 안이어야 한다. 이 검증이 없으면 run_command 가 통과해야 하는
        // SecurityValidator 블랙리스트를 통째로 우회할 수 있다(예: path=cmd.exe, arguments="/c ...").
        string fullPath;
        try
        {
            fullPath = ctx.Workspace.ResolvePath(path);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"path 가 작업 디렉토리를 벗어납니다: {ex.Message}"));
        }

        if (!File.Exists(fullPath))
            return Task.FromResult(ToolResult.Fail($"실행할 파일이 존재하지 않습니다: {path}"));

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
                FileName = fullPath,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc is null)
                return Task.FromResult(ToolResult.Fail($"프로세스를 시작하지 못했습니다: {path}"));

            return Task.FromResult(ToolResult.Json(new { started = true, pid = proc.Id, path = fullPath }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"프로세스 시작 실패: {ex.Message}"));
        }
    }
}
