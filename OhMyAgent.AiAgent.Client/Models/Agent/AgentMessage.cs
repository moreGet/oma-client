using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 하나의 대화 턴. AgentRequest.Messages 에 직렬화된다.
/// API_CONTRACT §4.1 의 message 객체에 대응.
/// </summary>
public sealed record AgentMessage
{
    [JsonPropertyName("role")]
    public required MessageRole Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>assistant 턴에서만 사용.</summary>
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>tool 턴에서만 사용.</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    /// <summary>tool 턴에서만 사용 — 도구 실행 실패 여부.</summary>
    [JsonPropertyName("is_error")]
    public bool? IsError { get; init; }

    public static AgentMessage System(string content) =>
        new() { Role = MessageRole.System, Content = content };

    public static AgentMessage User(string content) =>
        new() { Role = MessageRole.User, Content = content };

    public static AgentMessage Assistant(string? content, IReadOnlyList<ToolCall>? toolCalls = null) =>
        new() { Role = MessageRole.Assistant, Content = content, ToolCalls = toolCalls };

    public static AgentMessage ToolResultMsg(string toolCallId, string content, bool isError) =>
        new() { Role = MessageRole.Tool, ToolCallId = toolCallId, Content = content, IsError = isError };
}
