namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>에이전트 작업 계획 항목의 상태.</summary>
public enum TodoStatus
{
    Pending,
    InProgress,
    Completed
}

/// <summary>에이전트가 manage_todos 도구로 관리하는 작업 계획 한 항목(불변).</summary>
public sealed record TodoItem(string Content, TodoStatus Status, string? ActiveForm);
