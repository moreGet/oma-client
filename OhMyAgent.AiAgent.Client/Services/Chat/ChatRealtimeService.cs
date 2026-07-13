using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Chat;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// REST+WS 파사드(설계서 §3.3). 방/메시지 상태를 roomId→<see cref="RoomState"/> 딕셔너리로 보유.
/// message-id dedup(에코), 읽음 단조성(max), 안읽음 집계, 끊김 후 재동기화를 단독 소유한다.
/// 모든 상태변경 이벤트는 UiDispatch.InvokeAsync 경유로 UI 스레드에서 발화한다(D6).
/// 401 → AuthExpired, 403/404/429/5xx → OperationFailed.
/// </summary>
public sealed class ChatRealtimeService(
    IChatApiClient api,
    IChatSocketClient socket,
    ISettingsService settings) : IChatRealtimeService
{
    // roomId → 방 상태(메시지 목록/멤버별 읽음/presence/typing).
    private readonly ConcurrentDictionary<string, RoomState> _rooms = new();

    // memberId(UUID) → 표시이름. 캐시 미스 시 UUID 앞 ShortIdLength 자리 폴백.
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);
    private const int ShortIdLength = 8;

    // 안읽음 로컬 추적(roomId → count). 서버 /chat/unread 는 start·reconnect 에만 조회하고,
    // 그 외엔 WS 수신/읽음으로 로컬 증감 → 메시지마다 HTTP 를 쏘지 않는다(네트워크 절약).
    private readonly ConcurrentDictionary<string, int> _unread = new(StringComparer.Ordinal);

    // 1:1 상대 memberId 캐시(roomId → counterpart). 방 목록 재로드 시 GetMembers 재호출 회피.
    private readonly ConcurrentDictionary<string, string> _directCounterpart = new(StringComparer.Ordinal);

    // 현재 사용자가 보고 있는 방(열림+포커스). 이 방 수신분은 안읽음 증가 제외.
    private volatile string? _activeRoomId;

    // 읽음(POST /read) 디바운스 — roomId → 마지막 마크 tick. 스크롤마다 read POST 방지.
    private readonly ConcurrentDictionary<string, long> _lastMarkTick = new(StringComparer.Ordinal);
    private const long MarkReadDebounceMs = 2500;

    private bool _socketHooked;
    private ChatConnectionState _connectionState = ChatConnectionState.Disconnected;

    public ChatConnectionState ConnectionState => _connectionState;

    public event EventHandler<ChatConnectionState>? ConnectionStateChanged;
    public event EventHandler? AuthExpired;
    public event EventHandler<ChatOperationError>? OperationFailed;
    public event EventHandler<ChatRoom>? RoomUpserted;
    public event EventHandler<ChatUnreadResponse>? UnreadChanged;
    public event EventHandler<RoomMessageEvent>? MessageUpserted;
    public event EventHandler<WsReadPayload>? ReadChanged;
    public event EventHandler<WsTypingPayload>? TypingChanged;
    public event EventHandler<WsPresencePayload>? PresenceChanged;
    public event EventHandler<WsMemberPayload>? MemberJoined;
    public event EventHandler<WsMemberPayload>? MemberLeft;
    public event EventHandler? DirectoryUpdated;

    // ── 수명주기 ────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        HookSocket();

        // 1) 초기 전역 안읽음 배지 로드(REST). 방 목록은 셸 VM 의 LoadRoomsCommand 가 단독으로 로드한다
        //    (여기서 RoomUpserted 로 또 발화하면 이중 GetRooms 가 되므로 중복 제거).
        try
        {
            var unread = await api.GetUnreadAsync(ct).ConfigureAwait(false);
            SeedUnread(unread);
            await RaiseAsync(() => UnreadChanged?.Invoke(this, unread)).ConfigureAwait(false);
        }
        catch (ChatApiException ex) { HandleApiException(ex); }

        // 1b) 이름 디렉터리 prime(본인 /users/me + best-effort 디렉터리). 방/멤버 로드 전에 준비되도록 먼저.
        await PrimeDirectoryAsync(ct).ConfigureAwait(false);

        // 2) WS 연결(자동재연결 내장).
        await socket.ConnectAsync(ct).ConfigureAwait(false);
    }

    // ── 이름 디렉터리 (memberId → 표시이름) ────────────────────────

    public string DisplayName(string memberId)
    {
        if (string.IsNullOrEmpty(memberId)) return string.Empty;
        if (_names.TryGetValue(memberId, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;
        return memberId.Length > ShortIdLength ? memberId[..ShortIdLength] : memberId;   // 폴백: UUID 앞자리
    }

    public async Task<string?> GetDirectCounterpartAsync(string roomId, CancellationToken ct = default)
    {
        if (_directCounterpart.TryGetValue(roomId, out var cached))
            return cached;   // 캐시 — 방 목록 재로드 시 GetMembers 재호출 회피

        var members = await GetMembersAsync(roomId, ct).ConfigureAwait(false);
        var me = MyId();
        foreach (var m in members)
            if (!string.Equals(m, me, StringComparison.Ordinal))
            {
                _directCounterpart[roomId] = m;
                return m;
            }
        return null;
    }

    /// <summary>이름 캐시 채우기: 본인(/users/me) + best-effort 디렉터리(/members, 비admin 은 불가). 갱신 시 DirectoryUpdated.</summary>
    private async Task PrimeDirectoryAsync(CancellationToken ct)
    {
        var changed = false;
        try
        {
            var profile = await api.GetMyProfileAsync(ct).ConfigureAwait(false);
            var myName = profile is null ? null
                : !string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.DisplayName
                : profile.Username;
            if (!string.IsNullOrWhiteSpace(myName)) { _names[MyId()] = myName!; changed = true; }
        }
        catch { /* 본인 이름 실패는 무시 */ }

        try
        {
            var dir = await api.TryGetMemberDirectoryAsync(ct).ConfigureAwait(false);
            if (dir is not null)
                foreach (var kv in dir) { _names[kv.Key] = kv.Value; changed = true; }
        }
        catch { /* 디렉터리 실패(403 등)는 무시 — UUID 폴백 */ }

        if (changed)
            await RaiseAsync(() => DirectoryUpdated?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        UnhookSocket();
        await socket.DisconnectAsync().ConfigureAwait(false);
        _rooms.Clear();
        _unread.Clear();
        _directCounterpart.Clear();
        _lastMarkTick.Clear();
        _activeRoomId = null;
    }

    public async ValueTask DisposeAsync()
    {
        UnhookSocket();
        await socket.DisposeAsync().ConfigureAwait(false);
    }

    // ── REST 위임(상태 병합 포함) ────────────────────────

    public Task<IReadOnlyList<ChatRoom>> LoadRoomsAsync(CancellationToken ct = default)
        => ExecuteAsync(() => api.GetRoomsAsync(ct), Array.Empty<ChatRoom>());

    public async Task<IReadOnlyList<ChatMessage>> LoadMessagesAsync(string roomId, int limit = 50, string? before = null, CancellationToken ct = default)
    {
        try
        {
            var messages = await api.GetMessagesAsync(roomId, limit, before, ct).ConfigureAwait(false);
            var state = GetOrAddRoom(roomId);
            foreach (var m in messages)
                state.UpsertMessage(m);   // id dedup 병합
            return messages;
        }
        catch (ChatApiException ex)
        {
            HandleApiException(ex);
            return Array.Empty<ChatMessage>();
        }
    }

    public async Task<ChatRoom> OpenDirectRoomAsync(string userId, CancellationToken ct = default)
    {
        var room = await ExecuteThrowAsync(() => api.GetOrCreateDirectRoomAsync(userId, ct)).ConfigureAwait(false);
        GetOrAddRoom(room.Id);
        await RaiseAsync(() => RoomUpserted?.Invoke(this, room)).ConfigureAwait(false);
        return room;
    }

    public async Task<ChatRoom> CreateGroupRoomAsync(string name, IReadOnlyList<string> memberIds, CancellationToken ct = default)
    {
        var room = await ExecuteThrowAsync(() => api.CreateGroupRoomAsync(name, memberIds, ct)).ConfigureAwait(false);
        GetOrAddRoom(room.Id);
        await RaiseAsync(() => RoomUpserted?.Invoke(this, room)).ConfigureAwait(false);
        return room;
    }

    // ── 전송(WS 우선, REST 폴백) ────────────────────────

    public async Task SendMessageAsync(string roomId, string? content, IReadOnlyList<string>? mentions = null, IReadOnlyList<ChatAttachment>? attachments = null, CancellationToken ct = default)
    {
        // WS 연결 시 WS 송신(에코로 본인에게도 message 이벤트가 옴 → dedup 으로 치환).
        if (_connectionState == ChatConnectionState.Connected)
        {
            try
            {
                await socket.SendAsync(WsSendEnvelope.Create(roomId, content, mentions, attachments), ct).ConfigureAwait(false);
                return;
            }
            catch
            {
                // WS 송신 실패 → REST 폴백.
            }
        }

        try
        {
            var sent = await api.SendMessageAsync(roomId, new SendMessageRequest(content, mentions, attachments), ct).ConfigureAwait(false);
            ApplyIncomingMessage(sent, isEdited: false, isDeleted: false);
        }
        catch (ChatApiException ex)
        {
            HandleApiException(ex);
            throw;   // VM 이 낙관 메시지를 Failed 로 마킹할 수 있게 전파
        }
    }

    public Task EditMessageAsync(string roomId, string messageId, string content, CancellationToken ct = default)
        => ExecuteVoidAsync(async () =>
        {
            var edited = await api.EditMessageAsync(roomId, messageId, content, ct).ConfigureAwait(false);
            ApplyIncomingMessage(edited, isEdited: true, isDeleted: false);
        });

    public Task DeleteMessageAsync(string roomId, string messageId, CancellationToken ct = default)
        => ExecuteVoidAsync(() => api.DeleteMessageAsync(roomId, messageId, ct));

    public async Task SendTypingAsync(string roomId, TypingState state, CancellationToken ct = default)
    {
        if (_connectionState != ChatConnectionState.Connected)
            return;   // typing 은 휘발성 — 미연결 시 무시
        try { await socket.SendTypingAsync(roomId, state, ct).ConfigureAwait(false); }
        catch { /* typing 실패는 무시 */ }
    }

    public Task MarkReadAsync(string roomId, CancellationToken ct = default)
    {
        var had = _unread.TryGetValue(roomId, out var c) && c > 0;

        // 배지 즉시 0 (로컬, 무지연·무플리커).
        if (had)
        {
            _unread[roomId] = 0;
            _ = RaiseLocalUnreadAsync();
        }

        // POST 디바운스: 새 안읽음이 없는데 최근(MarkReadDebounceMs) 마크했으면 생략 — 스크롤마다 read POST 방지.
        // 새 안읽음(had)이 있으면 디바운스 무시하고 즉시 통지(읽음 표시 정확도 유지).
        var now = Environment.TickCount64;
        if (!had && _lastMarkTick.TryGetValue(roomId, out var last) && now - last < MarkReadDebounceMs)
            return Task.CompletedTask;
        _lastMarkTick[roomId] = now;

        return ExecuteVoidAsync(async () =>
        {
            var result = await api.MarkReadAsync(roomId, ct).ConfigureAwait(false);   // 서버 읽음 전진 + 타멤버에 read 브로드캐스트
            var state = GetOrAddRoom(roomId);
            state.AdvanceRead(MyId(), result.LastReadAt);
        });
    }

    // ── 단순 위임 ──

    public Task<IReadOnlyList<ChatReadReceipt>> GetReadsAsync(string roomId, CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var reads = await api.GetReadsAsync(roomId, ct).ConfigureAwait(false);
            var state = GetOrAddRoom(roomId);
            foreach (var r in reads)
                state.AdvanceRead(r.MemberId, r.LastReadAt);   // 단조성 병합
            return reads;
        }, Array.Empty<ChatReadReceipt>());

    public Task<IReadOnlyList<string>> GetMembersAsync(string roomId, CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            // ?detail=1 로 이름까지 받아 캐시(_names)를 채운다 — 일반 사용자도 방 멤버 이름이 즉시 표시된다
            // (기존 /members 디렉터리는 admin 전용). 반환값은 멤버 id 목록이라 호출부는 그대로 동작.
            var detailed = await api.GetRoomMembersDetailedAsync(roomId, ct).ConfigureAwait(false);
            await MergeNamesAsync(detailed).ConfigureAwait(false);
            return (IReadOnlyList<string>)detailed.Select(m => m.Id).ToList();
        }, Array.Empty<string>());

    /// <summary>?detail=1 결과를 이름 캐시에 병합한다(display_name 우선, 없으면 username). 변경 시 DirectoryUpdated.</summary>
    private async Task MergeNamesAsync(IReadOnlyList<ChatMemberDetail> members)
    {
        var changed = false;
        foreach (var m in members)
        {
            var name = !string.IsNullOrWhiteSpace(m.DisplayName) ? m.DisplayName!
                     : !string.IsNullOrWhiteSpace(m.Username) ? m.Username : null;
            if (name is null) continue;
            if (!_names.TryGetValue(m.Id, out var existing) || !string.Equals(existing, name, StringComparison.Ordinal))
            {
                _names[m.Id] = name;
                changed = true;
            }
        }
        if (changed)
            await RaiseAsync(() => DirectoryUpdated?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> AddMembersAsync(string roomId, IReadOnlyList<string> memberIds, CancellationToken ct = default)
        => ExecuteThrowAsync(() => api.AddMembersAsync(roomId, memberIds, ct));

    public Task KickMemberAsync(string roomId, string memberId, CancellationToken ct = default)
        => ExecuteVoidAsync(() => api.KickMemberAsync(roomId, memberId, ct));

    public Task LeaveRoomAsync(string roomId, CancellationToken ct = default)
        => ExecuteVoidAsync(() => api.LeaveRoomAsync(roomId, ct));

    public Task<IReadOnlyList<string>> GetPresenceAsync(string roomId, CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var online = await api.GetPresenceAsync(roomId, ct).ConfigureAwait(false);
            var state = GetOrAddRoom(roomId);
            state.SetPresence(online);
            return online;
        }, Array.Empty<string>());

    public Task<IReadOnlyList<ChatMessage>> GetMentionsAsync(int limit = 50, CancellationToken ct = default)
        => ExecuteAsync(() => api.GetMentionsAsync(limit, ct), Array.Empty<ChatMessage>());

    public Task<ChatAttachment> UploadAttachmentAsync(string filePath, CancellationToken ct = default)
        => ExecuteThrowAsync(() => api.UploadAttachmentAsync(filePath, ct));

    /// <summary>첨부 다운로드 — IChatApiClient 위임(상태 보유 없음). 실패 시 발화 후 재throw(UI 가 처리).</summary>
    public Task<Stream> DownloadAttachmentAsync(string attachmentId, CancellationToken ct = default)
        => ExecuteThrowAsync(() => api.DownloadAttachmentAsync(attachmentId, ct));

    // ── WS 이벤트 구독 → 집계 → 상위 event raise(UI 마샬) ────────────────────────

    private void HookSocket()
    {
        if (_socketHooked) return;
        _socketHooked = true;

        socket.StateChanged   += OnSocketStateChanged;
        socket.Reconnected    += OnReconnected;
        socket.MessageReceived += OnMessageReceived;
        socket.MessageEdited  += OnMessageEdited;
        socket.MessageDeleted += OnMessageDeleted;
        socket.ReadReceived   += OnReadReceived;
        socket.TypingReceived += OnTypingReceived;
        socket.MemberJoined   += OnMemberJoined;
        socket.MemberLeft     += OnMemberLeft;
        socket.PresenceChanged += OnPresenceChanged;
        socket.SocketError    += OnSocketError;
        socket.AuthRejected   += OnAuthRejected;
    }

    private void UnhookSocket()
    {
        if (!_socketHooked) return;
        _socketHooked = false;

        socket.StateChanged   -= OnSocketStateChanged;
        socket.Reconnected    -= OnReconnected;
        socket.MessageReceived -= OnMessageReceived;
        socket.MessageEdited  -= OnMessageEdited;
        socket.MessageDeleted -= OnMessageDeleted;
        socket.ReadReceived   -= OnReadReceived;
        socket.TypingReceived -= OnTypingReceived;
        socket.MemberJoined   -= OnMemberJoined;
        socket.MemberLeft     -= OnMemberLeft;
        socket.PresenceChanged -= OnPresenceChanged;
        socket.SocketError    -= OnSocketError;
        socket.AuthRejected   -= OnAuthRejected;
    }

    private void OnSocketStateChanged(object? sender, ChatConnectionState state)
    {
        _connectionState = state;
        _ = RaiseAsync(() => ConnectionStateChanged?.Invoke(this, state));
    }

    private void OnReconnected(object? sender, EventArgs e)
        => _ = ResyncAsync();

    private void OnMessageReceived(object? sender, ChatMessage m)
        => ApplyIncomingMessage(m, isEdited: false, isDeleted: false);

    private void OnMessageEdited(object? sender, ChatMessage m)
        => ApplyIncomingMessage(m, isEdited: true, isDeleted: false);

    private void OnMessageDeleted(object? sender, ChatMessage m)
        => ApplyIncomingMessage(m, isEdited: false, isDeleted: true);

    private void OnReadReceived(object? sender, WsReadPayload r)
    {
        var state = GetOrAddRoom(r.RoomId);
        state.AdvanceRead(r.MemberId, r.LastReadAt);   // 단조성
        _ = RaiseAsync(() => ReadChanged?.Invoke(this, r));
        // 내 읽음이 전진하면 안읽음이 줄 수 있으나, 전역 배지의 권위는 서버 /chat/unread.
    }

    private void OnTypingReceived(object? sender, WsTypingPayload t)
        => _ = RaiseAsync(() => TypingChanged?.Invoke(this, t));

    private void OnMemberJoined(object? sender, WsMemberPayload m)
        => _ = RaiseAsync(() => MemberJoined?.Invoke(this, m));

    private void OnMemberLeft(object? sender, WsMemberPayload m)
        => _ = RaiseAsync(() => MemberLeft?.Invoke(this, m));

    private void OnPresenceChanged(object? sender, WsPresencePayload p)
    {
        foreach (var state in _rooms.Values)
            state.SetMemberPresence(p.MemberId, p.Online);
        _ = RaiseAsync(() => PresenceChanged?.Invoke(this, p));
    }

    private void OnSocketError(object? sender, string error)
        => _ = RaiseAsync(() => OperationFailed?.Invoke(this, new ChatOperationError(0, "ws_error", error)));

    private void OnAuthRejected(object? sender, EventArgs e)
        => _ = RaiseAsync(() => AuthExpired?.Invoke(this, EventArgs.Empty));

    /// <summary>
    /// 신규/수정/삭제 메시지 적용. 에코 dedup: id 가 이미 있으면 갱신만. UI 마샬 후 MessageUpserted 발화.
    /// 안읽음 증분은 서버 권위(/chat/unread)에 위임하되, 남이 보낸 신규 메시지면 보조 신호로 전역 배지 재조회를 트리거하지 않고
    /// (과도한 호출 방지) 서버 read/unread 브로드캐스트에 의존한다.
    /// </summary>
    private void ApplyIncomingMessage(ChatMessage message, bool isEdited, bool isDeleted)
    {
        var state = GetOrAddRoom(message.RoomId);
        var isDeletedFinal = isDeleted || (message.Deleted ?? false);
        var isNew = state.UpsertMessage(message);   // id dedup 병합(신규면 true)

        _ = RaiseAsync(() => MessageUpserted?.Invoke(this, new RoomMessageEvent(message, isEdited, isDeletedFinal)));

        // 남이 보낸 신규 메시지면 안읽음 +1(로컬). 단 지금 보고 있는 방(_activeRoomId)은 제외(읽는 중).
        // 서버 /chat/unread 재조회 없이 로컬 카운터만 증가 → 메시지당 HTTP 0.
        if (isNew && !isEdited && !isDeletedFinal
            && !string.Equals(message.SenderId, MyId(), StringComparison.Ordinal)
            && !string.Equals(message.RoomId, _activeRoomId, StringComparison.Ordinal))
        {
            _unread.AddOrUpdate(message.RoomId, 1, (_, v) => v + 1);
            _ = RaiseLocalUnreadAsync();
        }
    }

    // ── 안읽음 로컬 추적 ──

    public void SetActiveRoom(string? roomId) => _activeRoomId = roomId;

    /// <summary>서버 /chat/unread 응답으로 로컬 카운터를 시드(start·reconnect 시 권위 동기화).</summary>
    private void SeedUnread(ChatUnreadResponse unread)
    {
        _unread.Clear();
        if (unread.Rooms is not null)
            foreach (var kv in unread.Rooms)
                _unread[kv.Key] = kv.Value;
    }

    /// <summary>로컬 카운터로 전역 안읽음 스냅샷을 만들어 발화(HTTP 없음).</summary>
    private Task RaiseLocalUnreadAsync()
    {
        var rooms = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var kv in _unread)
            if (kv.Value > 0) { rooms[kv.Key] = kv.Value; total += kv.Value; }
        var snapshot = new ChatUnreadResponse(total, rooms);
        return RaiseAsync(() => UnreadChanged?.Invoke(this, snapshot));
    }

    /// <summary>
    /// 끊김 후 재동기화(Reconnected/Start): 보유 방 각각 최신 메시지 page + 전체 unread + 각 방 reads 재조회 후 병합.
    /// id dedup 으로 중복 방지. 누락분만 상위로 발화.
    /// </summary>
    private async Task ResyncAsync()
    {
        // 1) 전역 안읽음 재조회(권위 재동기화).
        try
        {
            var unread = await api.GetUnreadAsync().ConfigureAwait(false);
            SeedUnread(unread);
            await RaiseAsync(() => UnreadChanged?.Invoke(this, unread)).ConfigureAwait(false);
        }
        catch (ChatApiException ex) { HandleApiException(ex); }

        // 2) 보유 방별 최신 메시지 + 읽음 재동기화.
        foreach (var roomId in _rooms.Keys.ToArray())
        {
            try
            {
                var messages = await api.GetMessagesAsync(roomId).ConfigureAwait(false);
                var state = GetOrAddRoom(roomId);
                foreach (var m in messages)
                {
                    var isNew = state.UpsertMessage(m);   // 신규만 true
                    if (isNew)
                    {
                        var captured = m;
                        await RaiseAsync(() =>
                            MessageUpserted?.Invoke(this, new RoomMessageEvent(captured, captured.EditedAt is not null, captured.Deleted ?? false)))
                            .ConfigureAwait(false);
                    }
                }

                var reads = await api.GetReadsAsync(roomId).ConfigureAwait(false);
                foreach (var r in reads)
                {
                    state.AdvanceRead(r.MemberId, r.LastReadAt);
                    var captured = r;
                    await RaiseAsync(() => ReadChanged?.Invoke(this, new WsReadPayload(roomId, captured.MemberId, captured.LastReadAt)))
                        .ConfigureAwait(false);
                }
            }
            catch (ChatApiException ex) { HandleApiException(ex); }
            catch { /* 단일 방 재동기화 실패는 무시(다음 방 진행) */ }
        }
    }

    // ── 오류 분기 + UI 마샬 + 실행 헬퍼 ────────────────────────

    private void HandleApiException(ChatApiException ex)
    {
        if (ex.StatusCode == 401)
        {
            _ = RaiseAsync(() => AuthExpired?.Invoke(this, EventArgs.Empty));
        }
        else
        {
            _ = RaiseAsync(() => OperationFailed?.Invoke(this, new ChatOperationError(ex.StatusCode, ex.Code, ex.Message)));
        }
    }

    /// <summary>결과 반환 + 실패 시 graceful fallback(오류는 OperationFailed/AuthExpired 로 발화).</summary>
    private async Task<T> ExecuteAsync<T>(Func<Task<T>> op, T fallback)
    {
        try { return await op().ConfigureAwait(false); }
        catch (ChatApiException ex) { HandleApiException(ex); return fallback; }
    }

    /// <summary>결과 반환 + 실패 시 발화 후 재throw(호출자 UI 가 추가 처리).</summary>
    private async Task<T> ExecuteThrowAsync<T>(Func<Task<T>> op)
    {
        try { return await op().ConfigureAwait(false); }
        catch (ChatApiException ex) { HandleApiException(ex); throw; }
    }

    /// <summary>void 작업 + 실패 시 발화(graceful, 재throw 없음).</summary>
    private async Task ExecuteVoidAsync(Func<Task> op)
    {
        try { await op().ConfigureAwait(false); }
        catch (ChatApiException ex) { HandleApiException(ex); }
    }

    private static Task RaiseAsync(Action action) => UiDispatch.InvokeAsync(action);

    private RoomState GetOrAddRoom(string roomId) => _rooms.GetOrAdd(roomId, _ => new RoomState());

    /// <summary>
    /// 현재 사용자 식별자(member UUID). 서버는 멤버 id 를 sender_id/member_id/user_id 로 내려주며
    /// 이는 JWT(AuthToken) 의 <c>sub</c> 클레임과 동일하다(/users/me 에는 id 필드 없음 — 라이브 검증됨).
    /// 따라서 VM currentUserId 와 동일 소스가 되도록 <see cref="JwtIdentity.MemberId"/> 로 얻는다.
    /// (안읽음 전역 배지는 서버 /chat/unread 가 권위 — 여기서의 내 읽음 추적은 보조 용도.)
    /// </summary>
    private string MyId() => JwtIdentity.MemberId(settings.Current.AuthToken);

    // ── 방 단위 상태(메모리 보유). 안읽음 정의: 내 last_read 이후 남의 비삭제 메시지 수. ────────────────────────
    private sealed class RoomState
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ChatMessage> _messages = new();   // id → message
        private readonly Dictionary<string, long> _lastReadByMember = new();  // member → last_read_at(단조)
        private readonly HashSet<string> _online = new();

        /// <summary>id dedup 병합. 신규면 true, 기존 갱신이면 false.</summary>
        public bool UpsertMessage(ChatMessage message)
        {
            lock (_gate)
            {
                var isNew = !_messages.ContainsKey(message.Id);
                _messages[message.Id] = message;
                return isNew;
            }
        }

        /// <summary>읽음 단조성: max(old,new). 전진만.</summary>
        public void AdvanceRead(string memberId, long lastReadAt)
        {
            if (string.IsNullOrEmpty(memberId)) return;
            lock (_gate)
            {
                if (_lastReadByMember.TryGetValue(memberId, out var prev) && prev >= lastReadAt)
                    return;   // 후진 무시
                _lastReadByMember[memberId] = lastReadAt;
            }
        }

        public void SetPresence(IReadOnlyList<string> online)
        {
            lock (_gate)
            {
                _online.Clear();
                foreach (var id in online) _online.Add(id);
            }
        }

        public void SetMemberPresence(string memberId, bool online)
        {
            lock (_gate)
            {
                if (online) _online.Add(memberId);
                else _online.Remove(memberId);
            }
        }

        /// <summary>안읽음(내 last_read 이후 남이 보낸 비삭제 메시지 수). 전역 배지의 보조 추정.</summary>
        public int CountUnread(string myId)
        {
            lock (_gate)
            {
                var myRead = _lastReadByMember.TryGetValue(myId, out var v) ? v : 0;
                var count = 0;
                foreach (var m in _messages.Values)
                {
                    if (m.SenderId == myId) continue;       // 내가 보낸 것 제외
                    if (m.Deleted ?? false) continue;       // 삭제 제외
                    if (m.CreatedAt > myRead) count++;
                }
                return count;
            }
        }
    }
}
