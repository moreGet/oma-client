using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models.Chat;

// ── WebSocket envelope (설계서 §2). 인바운드는 type peek 후 해당 payload 역직렬화. ────────────────────────

// ── 아웃바운드 (클라→서버) ──

public sealed record WsSendEnvelope(
    [property: JsonPropertyName("type")]        string Type,          // "send"
    [property: JsonPropertyName("room_id")]     string RoomId,
    [property: JsonPropertyName("content")]     string? Content,
    [property: JsonPropertyName("mentions")]    IReadOnlyList<string>? Mentions,
    [property: JsonPropertyName("attachments")] IReadOnlyList<ChatAttachment>? Attachments)
{
    public static WsSendEnvelope Create(
        string roomId,
        string? content,
        IReadOnlyList<string>? mentions = null,
        IReadOnlyList<ChatAttachment>? att = null)
        => new("send", roomId, content, mentions, att);
}

public sealed record WsTypingEnvelope(
    [property: JsonPropertyName("type")]    string Type,              // "typing"
    [property: JsonPropertyName("room_id")] string RoomId,
    [property: JsonPropertyName("state")]   TypingState State)
{
    public static WsTypingEnvelope Create(string roomId, TypingState s) => new("typing", roomId, s);
}

// ── 인바운드 (서버→클라) — type 디스패치 후 해당 payload 역직렬화 ──
// (1차 type peek 는 ChatSocketClient 가 JsonDocument 로 인라인 처리 — 전용 DTO 불필요.)

/// <summary>message / message_edited / message_deleted 공용.</summary>
public sealed record WsMessageEvent(
    [property: JsonPropertyName("message")] ChatMessage Message);

public sealed record WsReadEvent(
    [property: JsonPropertyName("read")] WsReadPayload Read);

public sealed record WsReadPayload(
    [property: JsonPropertyName("room_id")]      string RoomId,
    [property: JsonPropertyName("member_id")]    string MemberId,
    [property: JsonPropertyName("last_read_at")] long LastReadAt);

public sealed record WsTypingEvent(
    [property: JsonPropertyName("typing")] WsTypingPayload Typing);

public sealed record WsTypingPayload(
    [property: JsonPropertyName("room_id")]   string RoomId,
    [property: JsonPropertyName("member_id")] string MemberId,
    [property: JsonPropertyName("state")]     TypingState State);

/// <summary>member_joined / member_left 공용.</summary>
public sealed record WsMemberEvent(
    [property: JsonPropertyName("member")] WsMemberPayload Member);

public sealed record WsMemberPayload(
    [property: JsonPropertyName("room_id")]   string RoomId,
    [property: JsonPropertyName("member_id")] string MemberId);

public sealed record WsPresenceEvent(
    [property: JsonPropertyName("presence")] WsPresencePayload Presence);

public sealed record WsPresencePayload(
    [property: JsonPropertyName("member_id")] string MemberId,
    [property: JsonPropertyName("online")]    bool Online);

/// <summary>{"type":"error","error":"..."} — 그 연결로만 옴(브로드캐스트 아님).</summary>
public sealed record WsErrorEvent(
    [property: JsonPropertyName("error")] string Error);

// ChatJson.Options — AgentJson.Options 복제(ToolCallJsonConverter 제외). snake_case enum +
// WhenWritingNull + Web defaults, DTO 의 [JsonPropertyName] 으로 프로퍼티명 처리.
public static class ChatJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
