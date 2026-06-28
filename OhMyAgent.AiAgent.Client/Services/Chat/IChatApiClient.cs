using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Chat;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// Chat REST 전부(설계서 §3.1). 라우트 `/api/v1/chat/...`. 에러는 <see cref="ChatApiException"/>(상태코드 보존)로 throw.
/// </summary>
public interface IChatApiClient
{
    // 방
    Task<IReadOnlyList<ChatRoom>> GetRoomsAsync(CancellationToken ct = default);
    Task<ChatRoom> CreateGroupRoomAsync(string name, IReadOnlyList<string> memberIds, CancellationToken ct = default);
    Task<ChatRoom> GetOrCreateDirectRoomAsync(string userId, CancellationToken ct = default);

    // 메시지
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string roomId, int limit = 50, string? before = null, CancellationToken ct = default);
    Task<ChatMessage> SendMessageAsync(string roomId, SendMessageRequest body, CancellationToken ct = default);   // REST 경로(WS 불가 시 폴백)
    Task<ChatMessage> EditMessageAsync(string roomId, string messageId, string content, CancellationToken ct = default);
    Task DeleteMessageAsync(string roomId, string messageId, CancellationToken ct = default);                     // 멱등

    // 읽음
    Task<ChatReadResult> MarkReadAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatReadReceipt>> GetReadsAsync(string roomId, CancellationToken ct = default);
    Task<ChatUnreadResponse> GetUnreadAsync(CancellationToken ct = default);

    // 멤버 / presence
    Task<IReadOnlyList<string>> GetMembersAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> AddMembersAsync(string roomId, IReadOnlyList<string> memberIds, CancellationToken ct = default);  // group 한정
    Task KickMemberAsync(string roomId, string memberId, CancellationToken ct = default);                          // 생성자/group 한정
    Task LeaveRoomAsync(string roomId, CancellationToken ct = default);                                            // group 한정
    Task<IReadOnlyList<string>> GetPresenceAsync(string roomId, CancellationToken ct = default);

    // 멘션 / 첨부
    Task<IReadOnlyList<ChatMessage>> GetMentionsAsync(int limit = 50, CancellationToken ct = default);
    Task<ChatAttachment> UploadAttachmentAsync(string filePath, CancellationToken ct = default);                  // multipart, part="file", ≤10MiB
    Task<Stream> DownloadAttachmentAsync(string attachmentId, CancellationToken ct = default);                    // 바이너리
}
