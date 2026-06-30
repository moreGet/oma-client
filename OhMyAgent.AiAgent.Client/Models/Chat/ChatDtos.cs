using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models.Chat;

// ─────────────────────────────────────────────────────────────────────────────
// REST/WS DTO (설계서 §2). 모든 시각 = long(unix epoch 초). UI 표시 시점에만 변환.
// 직렬화 옵션 = ChatJson.Options (ChatWireEnvelopes.cs).
// ─────────────────────────────────────────────────────────────────────────────

// ── 코어 엔티티 ──

public sealed record ChatRoom(
    [property: JsonPropertyName("id")]           string Id,
    [property: JsonPropertyName("type")]         ChatRoomType Type,
    [property: JsonPropertyName("name")]         string? Name,        // 1:1은 null 가능
    [property: JsonPropertyName("created_at")]   long CreatedAt,
    [property: JsonPropertyName("unread_count")] int UnreadCount);

public sealed record ChatMessage(
    [property: JsonPropertyName("id")]          string Id,
    [property: JsonPropertyName("room_id")]     string RoomId,
    [property: JsonPropertyName("sender_id")]   string SenderId,
    [property: JsonPropertyName("content")]     string Content,
    [property: JsonPropertyName("created_at")]  long CreatedAt,
    [property: JsonPropertyName("edited_at")]   long? EditedAt,
    [property: JsonPropertyName("deleted")]     bool? Deleted,
    // ⚠️ REST 이력 응답(GET /chat/rooms/{id}/messages)에는 아래 둘이 누락된다(WS message DTO 에만 존재).
    //    → 이력 로드 시 null 로 역직렬화됨. 멘션/첨부는 WS 실시간 수신분만 렌더(SSOT 임시 대응).
    [property: JsonPropertyName("mentions")]    IReadOnlyList<string>? Mentions,
    [property: JsonPropertyName("attachments")] IReadOnlyList<ChatAttachment>? Attachments);

public sealed record ChatAttachment(
    [property: JsonPropertyName("id")]           string? Id,           // 업로드 응답엔 존재, send 본문엔 미포함
    [property: JsonPropertyName("file_name")]    string FileName,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("size_bytes")]   long SizeBytes,
    [property: JsonPropertyName("url")]          string Url);

public sealed record ChatReadReceipt(
    [property: JsonPropertyName("member_id")]    string MemberId,
    [property: JsonPropertyName("last_read_at")] long LastReadAt);

// ── 컨테이너 응답 DTO ──

public sealed record ChatRoomsResponse(
    [property: JsonPropertyName("rooms")] IReadOnlyList<ChatRoom> Rooms);

public sealed record ChatMessagesResponse(
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

public sealed record ChatReadsResponse(
    [property: JsonPropertyName("reads")] IReadOnlyList<ChatReadReceipt> Reads);

public sealed record ChatMembersResponse(
    [property: JsonPropertyName("members")] IReadOnlyList<string> Members);

/// <summary>GET /api/v1/chat/rooms/{id}/members?detail=1 의 멤버 항목(이름 포함).</summary>
public sealed record ChatMemberDetail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("display_name")] string? DisplayName);

public sealed record ChatMembersDetailResponse(
    [property: JsonPropertyName("members")] IReadOnlyList<ChatMemberDetail> Members);

public sealed record ChatPresenceResponse(
    [property: JsonPropertyName("online")] IReadOnlyList<string> Online);

public sealed record ChatUnreadResponse(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("rooms")] IReadOnlyDictionary<string, int> Rooms);   // {roomId:count}, count>0 만

public sealed record ChatReadResult(
    [property: JsonPropertyName("room_id")]      string RoomId,
    [property: JsonPropertyName("last_read_at")] long LastReadAt);

// ── 요청 본문 DTO ──

public sealed record CreateGroupRoomRequest(
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("member_ids")] IReadOnlyList<string> MemberIds);

public sealed record CreateDirectRoomRequest(
    [property: JsonPropertyName("user_id")] string UserId);

public sealed record SendMessageRequest(
    [property: JsonPropertyName("content")]     string? Content,
    [property: JsonPropertyName("mentions")]    IReadOnlyList<string>? Mentions,
    [property: JsonPropertyName("attachments")] IReadOnlyList<ChatAttachment>? Attachments);   // content·attachments 둘 다 null 이면 서버 400

public sealed record EditMessageRequest(
    [property: JsonPropertyName("content")] string Content);

public sealed record AddMembersRequest(
    [property: JsonPropertyName("member_ids")] IReadOnlyList<string> MemberIds);
