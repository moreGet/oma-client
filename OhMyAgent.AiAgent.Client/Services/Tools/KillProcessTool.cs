using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class KillProcessTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"pid":{"type":"integer"},"name":{"type":"string"}}}
        """);

    public string Name => "kill_process";
    public string Description => "Terminate a process by 'pid' or by 'name' (all matching processes). Destructive — requires approval.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Destructive;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var pid = ToolSchemas.GetInt(args, "pid");
        var name = ToolSchemas.GetString(args, "name");

        if (pid is null && string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Fail("pid 또는 name 중 하나는 지정해야 합니다."));

        var killed = new List<object>();
        var errors = new List<string>();

        if (pid is not null)
        {
            try
            {
                using var proc = Process.GetProcessById(pid.Value);
                var procName = proc.ProcessName;
                proc.Kill(entireProcessTree: true);
                killed.Add(new { pid = pid.Value, name = procName });
            }
            catch (Exception ex)
            {
                errors.Add($"pid {pid.Value}: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var trimmed = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name;

            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(trimmed);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail($"프로세스 조회 실패: {ex.Message}"));
            }

            if (matches.Length == 0 && pid is null)
                return Task.FromResult(ToolResult.Fail($"이름과 일치하는 프로세스가 없습니다: {name}"));

            foreach (var proc in matches)
            {
                try
                {
                    var id = proc.Id;
                    proc.Kill(entireProcessTree: true);
                    killed.Add(new { pid = id, name = trimmed });
                }
                catch (Exception ex)
                {
                    errors.Add($"{trimmed} ({proc.Id}): {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        if (killed.Count == 0)
            return Task.FromResult(ToolResult.Fail($"종료한 프로세스가 없습니다. {string.Join("; ", errors)}"));

        return Task.FromResult(ToolResult.Json(new { killed_count = killed.Count, killed, errors }));
    }
}
