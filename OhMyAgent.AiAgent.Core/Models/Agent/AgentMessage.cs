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

    /// <summary>
    /// assistant 턴의 확장 사고 원문(사고 미사용 시 null). 재생용 — 사고가 켜진 채 도구를 쓴 턴은
    /// 다음 요청에 이 사고 블록을 되돌려 보내야 서버가 400 을 내지 않는다. null 이면 직렬화 생략.
    /// </summary>
    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Thinking { get; init; }

    /// <summary>그 사고 블록의 서명(변조 검증용). 서버가 바이트 그대로 요구하므로 손대지 않는다.</summary>
    [JsonPropertyName("thinking_signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThinkingSignature { get; init; }

    /// <summary>
    /// 예약 필드 — 클라이언트 첨부 메타. 서버 소비는 미래(API_CONTRACT §8). null이면 직렬화 생략.
    /// AgentJson.Options(WhenWritingNull)이므로 첨부 없으면 바이트 단위로 기존 요청 불변.
    /// </summary>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<Attachment>? Attachments { get; init; }

    public static AgentMessage System(string content) =>
        new() { Role = MessageRole.System, Content = content };

    public static AgentMessage User(string content, IReadOnlyList<Attachment>? attachments = null) =>
        new() { Role = MessageRole.User, Content = content, Attachments = attachments };

    public static AgentMessage Assistant(string? content, IReadOnlyList<ToolCall>? toolCalls = null,
        string? thinking = null, string? thinkingSignature = null) =>
        new()
        {
            Role = MessageRole.Assistant,
            Content = content,
            ToolCalls = toolCalls,
            // 서명 없는 사고는 재생 불가라 서버가 거부한다 → 둘 다 있을 때만 싣는다.
            Thinking = string.IsNullOrEmpty(thinkingSignature) ? null : thinking,
            ThinkingSignature = string.IsNullOrEmpty(thinkingSignature) ? null : thinkingSignature,
        };

    public static AgentMessage ToolResultMsg(string toolCallId, string content, bool isError) =>
        new() { Role = MessageRole.Tool, ToolCallId = toolCallId, Content = content, IsError = isError };
}
