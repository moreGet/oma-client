using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 워크스페이스 안의 실행 파일을 띄운다. <paramref name="activities"/> 는 선택적 관찰자다(null 이면 종전과 동일).
///
/// 이 도구는 fire-and-forget 이다 — 도구가 반환한 뒤에도 프로세스는 계속 산다. 그래서 태스크 매니저에는
/// "해제 시점이 없는" 항목으로 등록하고(<see cref="IAgentActivityRegistry.TrackDetachedProcess"/>),
/// 소유 도구가 끝난 뒤에도 살아 있는 이 항목이 곧 <b>고아 프로세스 정리의 실제 대상</b>이 된다.
/// </summary>
public sealed class StartProcessTool(IAgentActivityRegistry? activities = null) : ITool
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

            Track(proc, fullPath, arguments);

            return Task.FromResult(ToolResult.Json(new { started = true, pid = proc.Id, path = fullPath }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"프로세스 시작 실패: {ex.Message}"));
        }
    }

    /// <summary>
    /// 태스크 매니저에 등록한다. 관찰 전용이라 <b>어떤 실패도 도구 결과에 영향을 주지 않는다</b> —
    /// 프로세스는 이미 떴고, 등록 실패로 그것을 되돌리면 오히려 사용자가 의도한 동작을 깨뜨린다.
    /// 시작 시각을 못 읽으면 null 로 남기고, 그 항목은 신원 확인 불가로 강제 종료 대상에서 제외된다.
    /// </summary>
    private void Track(Process proc, string fullPath, string? arguments)
    {
        if (activities is null) return;

        try
        {
            DateTimeOffset? startedAt = null;
            try { startedAt = new DateTimeOffset(proc.StartTime).ToUniversalTime(); }
            catch (Exception) { /* 승격/타 계정 프로세스는 조회가 거부될 수 있다 */ }

            var detail = string.IsNullOrWhiteSpace(arguments) ? fullPath : $"{fullPath} {arguments}";
            activities.TrackDetachedProcess(
                new TrackedProcessIdentity(proc.Id, SafeName(proc), startedAt), detail);
        }
        catch (Exception ex)
        {
            AppLog.Warn("StartProcessTool", $"자식 프로세스 추적 등록 실패: {ex.Message}");
        }
    }

    private static string SafeName(Process proc)
    {
        try { return proc.ProcessName; }
        catch (Exception) { return string.Empty; }
    }
}
