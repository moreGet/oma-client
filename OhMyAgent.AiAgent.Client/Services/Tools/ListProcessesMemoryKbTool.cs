using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class ListProcessesMemoryKbTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"name_filter":{"type":"string"}}}
        """);

    public string Name => "list_processes_memory_kb";
    public string Description => "List running processes with name, PID, and working-set memory (KB). Optionally filter by 'name_filter' (case-insensitive substring).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var filter = ToolSchemas.GetString(args, "name_filter");

        var processes = Process.GetProcesses()
            .Select(p =>
            {
                string name;
                long memory;
                int pid;
                try
                {
                    name = p.ProcessName;
                    pid = p.Id;
                    memory = p.WorkingSet64;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    p.Dispose();
                }
                return new { name, pid, memory_kb = memory / 1024 };
            })
            .Where(p => p is not null)
            .Where(p => string.IsNullOrEmpty(filter)
                        || p!.name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p!.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(ToolResult.Json(new { count = processes.Count, processes }));
    }
}
