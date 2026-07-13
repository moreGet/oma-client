using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 다단계 작업의 계획을 추적하는 메타 도구. 모델이 전체 todo 목록을 넘기면 공유 저장소를 교체하고 UI에 반영된다.
/// 시스템 부작용이 없어 위험도 ReadOnly.
/// </summary>
public sealed class ManageTodosTool(ITodoService todos) : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"todos":{"type":"array","items":{"type":"object","properties":{"content":{"type":"string","description":"할 일(명령형, 예: '설정 파일 읽기')"},"status":{"type":"string","enum":["pending","in_progress","completed"]},"activeForm":{"type":"string","description":"진행 중 표시형(예: '설정 파일 읽는 중')"}},"required":["content","status"]}}},"required":["todos"]}
        """);

    public string Name => "manage_todos";

    public string Description =>
        "Track your task plan for multi-step work. ALWAYS pass the FULL todo list — it replaces the previous list entirely. " +
        "Keep exactly one item 'in_progress' while working on it, and mark it 'completed' the moment it is done (do not batch completions). " +
        "Use this for any task with 3+ distinct steps; skip it for trivial single-step tasks.";

    public JsonElement ParametersSchema => Schema;

    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (!args.TryGetProperty("todos", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Task.FromResult(ToolResult.Fail("'todos' 배열이 필요합니다."));

        var list = new List<TodoItem>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var content = ToolSchemas.GetString(el, "content");
            if (string.IsNullOrWhiteSpace(content)) continue;
            var status = MapStatus(ToolSchemas.GetString(el, "status"));
            var activeForm = ToolSchemas.GetString(el, "activeForm");
            list.Add(new TodoItem(content!, status, activeForm));
        }

        todos.Write(list);

        var done = list.Count(t => t.Status == TodoStatus.Completed);
        var running = list.FirstOrDefault(t => t.Status == TodoStatus.InProgress);
        var now = running is null ? "" : $" · 진행: {running.Content}";
        return Task.FromResult(ToolResult.Ok($"할 일 {list.Count}개 갱신됨(완료 {done}개){now}."));
    }

    private static TodoStatus MapStatus(string? s) => s switch
    {
        "in_progress" => TodoStatus.InProgress,
        "completed"   => TodoStatus.Completed,
        _             => TodoStatus.Pending
    };
}
