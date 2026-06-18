using System.Text.Json;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// ITool.ExecuteAsync 의 반환값. Content 는 tool 메시지의 content 가 된다.
/// </summary>
public sealed record ToolResult(string Content, bool IsError)
{
    public static ToolResult Ok(string content) => new(content, false);

    public static ToolResult Fail(string message) => new(message, true);

    /// <summary>구조화된 객체를 JSON content 로 직렬화.</summary>
    public static ToolResult Json(object payload) =>
        new(JsonSerializer.Serialize(payload, AgentJson.Options), false);
}
