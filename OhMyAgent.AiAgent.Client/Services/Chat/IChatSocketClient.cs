using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Chat;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// Chat WebSocket 래퍼(설계서 §3.2). 연결/송신/이벤트(C# event)/자동재연결(지수 backoff)/ping-pong/수신 펌프.
/// 재동기화·dedup·집계는 상위 <see cref="IChatRealtimeService"/> 가 단독 소유한다(이 클라이언트는 raw event 만 노출).
/// </summary>
public interface IChatSocketClient : IAsyncDisposable
{
    ChatConnectionState State { get; }

    event EventHandler<ChatConnectionState>? StateChanged;
    event EventHandler? Reconnected;                        // 끊김→재연결 성공(이력 재동기화 신호)
    event EventHandler<ChatMessage>? MessageReceived;       // message
    event EventHandler<ChatMessage>? MessageEdited;         // message_edited
    event EventHandler<ChatMessage>? MessageDeleted;        // message_deleted
    event EventHandler<WsReadPayload>? ReadReceived;        // read
    event EventHandler<WsTypingPayload>? TypingReceived;    // typing (발신자 제외)
    event EventHandler<WsMemberPayload>? MemberJoined;      // member_joined
    event EventHandler<WsMemberPayload>? MemberLeft;        // member_left
    event EventHandler<WsPresencePayload>? PresenceChanged; // presence
    event EventHandler<string>? SocketError;               // {"type":"error"} (그 연결로만)
    event EventHandler? AuthRejected;                      // 핸드셰이크 401/403 → 상위가 재로그인 분기

    Task ConnectAsync(CancellationToken ct = default);
    Task SendAsync(WsSendEnvelope envelope, CancellationToken ct = default);
    Task SendTypingAsync(string roomId, TypingState state, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);  // 자동재연결 중지
}
