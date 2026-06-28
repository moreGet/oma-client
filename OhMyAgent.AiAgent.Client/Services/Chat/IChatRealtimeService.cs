using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Chat;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>403/404/429/5xx 등 비치명 운영 오류(표시 전용).</summary>
public sealed record ChatOperationError(int StatusCode, string Code, string Message);

/// <summary>신규/수정/삭제 통합 메시지 이벤트(IsEdited/IsDeleted 플래그).</summary>
public sealed record RoomMessageEvent(ChatMessage Message, bool IsEdited, bool IsDeleted);

/// <summary>
/// REST+WS 파사드(설계서 §3.3). VM 의 단일 의존. 방/메시지 상태를 딕셔너리로 보유하고
/// message-id dedup(에코), 읽음 단조성(max), 안읽음 집계, 끊김 후 재동기화를 단독 소유한다.
/// 모든 상태변경 이벤트는 UI 스레드 마샬(UiDispatch.InvokeAsync) 후 발화한다.
/// </summary>
public interface IChatRealtimeService : IAsyncDisposable
{
    ChatConnectionState ConnectionState { get; }

    // 상위(셸 VM)가 구독하는 집계 이벤트 — 전부 UI 스레드 마샬 후 발화.
    event EventHandler<ChatConnectionState>? ConnectionStateChanged;
    event EventHandler? AuthExpired;                                  // 401 전용 → App.ReturnToLogin
    event EventHandler<ChatOperationError>? OperationFailed;          // 403/404/429/5xx — 표시만
    event EventHandler<ChatRoom>? RoomUpserted;                       // 새 방/안읽음 변동
    event EventHandler<ChatUnreadResponse>? UnreadChanged;            // 전역 배지
    event EventHandler<RoomMessageEvent>? MessageUpserted;            // 신규/수정/삭제 통합
    event EventHandler<WsReadPayload>? ReadChanged;
    event EventHandler<WsTypingPayload>? TypingChanged;
    event EventHandler<WsPresencePayload>? PresenceChanged;
    event EventHandler<WsMemberPayload>? MemberJoined;
    event EventHandler<WsMemberPayload>? MemberLeft;

    Task StartAsync(CancellationToken ct = default);                 // unread 초기로드 + 방목록 + WS connect
    Task StopAsync();

    Task<IReadOnlyList<ChatRoom>> LoadRoomsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> LoadMessagesAsync(string roomId, int limit = 50, string? before = null, CancellationToken ct = default);
    Task<ChatRoom> OpenDirectRoomAsync(string userId, CancellationToken ct = default);
    Task<ChatRoom> CreateGroupRoomAsync(string name, IReadOnlyList<string> memberIds, CancellationToken ct = default);

    // 전송: WS 우선, 미연결 시 REST 폴백.
    Task SendMessageAsync(string roomId, string? content, IReadOnlyList<string>? mentions = null, IReadOnlyList<ChatAttachment>? attachments = null, CancellationToken ct = default);
    Task EditMessageAsync(string roomId, string messageId, string content, CancellationToken ct = default);
    Task DeleteMessageAsync(string roomId, string messageId, CancellationToken ct = default);
    Task SendTypingAsync(string roomId, TypingState state, CancellationToken ct = default);
    Task MarkReadAsync(string roomId, CancellationToken ct = default);

    Task<IReadOnlyList<ChatReadReceipt>> GetReadsAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetMembersAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> AddMembersAsync(string roomId, IReadOnlyList<string> memberIds, CancellationToken ct = default);
    Task KickMemberAsync(string roomId, string memberId, CancellationToken ct = default);
    Task LeaveRoomAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPresenceAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessage>> GetMentionsAsync(int limit = 50, CancellationToken ct = default);
    Task<ChatAttachment> UploadAttachmentAsync(string filePath, CancellationToken ct = default);

    /// <summary>첨부 다운로드(바이너리 스트림). IChatApiClient 위임 — UI(저장 다이얼로그)에서 사용.</summary>
    Task<Stream> DownloadAttachmentAsync(string attachmentId, CancellationToken ct = default);
}
