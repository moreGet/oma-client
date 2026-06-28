using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Chat;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// <see cref="ClientWebSocket"/> 기반 Chat WebSocket 클라이언트(설계서 §3.2).
/// URL = ServerBaseUrl(http→ws / https→wss) + /api/v1/chat/ws. 핸드셰이크에 Bearer 헤더.
/// 백그라운드 수신 펌프(텍스트 프레임 누적 → type peek → 해당 event raise), 지수 backoff 자동재연결,
/// ping-pong(KeepAliveInterval). 이벤트는 그대로 raw 노출(집계는 상위 파사드).
/// </summary>
public sealed class ChatSocketClient(ISettingsService settings) : IChatSocketClient
{
    private const string WsPath = "/api/v1/chat/ws";

    // 재연결 backoff 파라미터.
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAlive  = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Random _jitter = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _lifecycleCts;   // 전체 수명(Disconnect/Dispose 시 취소)
    private Task? _pumpTask;
    private volatile bool _intentionalClose;
    private bool _hasConnectedBefore;                 // 첫 연결 성공 후 재연결 시 Reconnected 발화 판단
    private ChatConnectionState _state = ChatConnectionState.Disconnected;

    public ChatConnectionState State => _state;

    public event EventHandler<ChatConnectionState>? StateChanged;
    public event EventHandler? Reconnected;
    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatMessage>? MessageEdited;
    public event EventHandler<ChatMessage>? MessageDeleted;
    public event EventHandler<WsReadPayload>? ReadReceived;
    public event EventHandler<WsTypingPayload>? TypingReceived;
    public event EventHandler<WsMemberPayload>? MemberJoined;
    public event EventHandler<WsMemberPayload>? MemberLeft;
    public event EventHandler<WsPresencePayload>? PresenceChanged;
    public event EventHandler<string>? SocketError;
    public event EventHandler? AuthRejected;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (_pumpTask is { IsCompleted: false })
            return Task.CompletedTask;   // 이미 연결/연결중

        _intentionalClose = false;
        _lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pumpTask = Task.Run(() => RunConnectionLoopAsync(_lifecycleCts.Token));
        return Task.CompletedTask;
    }

    public async Task SendAsync(WsSendEnvelope envelope, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(envelope, ChatJson.Options);
        await SendRawAsync(json, ct).ConfigureAwait(false);
    }

    public async Task SendTypingAsync(string roomId, TypingState state, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(WsTypingEnvelope.Create(roomId, state), ChatJson.Options);
        await SendRawAsync(json, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _intentionalClose = true;
        if (_lifecycleCts is not null)
            await _lifecycleCts.CancelAsync().ConfigureAwait(false);

        var pump = _pumpTask;
        var socket = _ws;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client disconnect", ct).ConfigureAwait(false);
            }
            catch { /* 닫는 중 오류 무시 */ }
        }

        if (pump is not null)
        {
            try { await pump.ConfigureAwait(false); }
            catch { /* 펌프 종료 예외 무시 */ }
        }

        _pumpTask = null;
        SetState(ChatConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _ws?.Dispose();
        _lifecycleCts?.Dispose();
        _sendLock.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 연결 루프 + 수신 펌프 + 자동재연결
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunConnectionLoopAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested && !_intentionalClose)
        {
            SetState(_hasConnectedBefore ? ChatConnectionState.Reconnecting : ChatConnectionState.Connecting);

            ClientWebSocket ws = new();
            ws.Options.KeepAliveInterval = KeepAlive;   // 서버 ping → BCL 자동 pong

            var token = settings.Current.AuthToken;
            if (!string.IsNullOrWhiteSpace(token))
                ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

            var uri = BuildWsUri();

            try
            {
                await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ws.Dispose();
                break;
            }
            catch (WebSocketException wex) when (IsAuthRejection(wex))
            {
                // 핸드셰이크 401/403 → 재로그인 분기(자동재연결 중지).
                ws.Dispose();
                AuthRejected?.Invoke(this, EventArgs.Empty);
                SetState(ChatConnectionState.Disconnected);
                return;
            }
            catch
            {
                // 연결 실패 → backoff 후 재시도.
                ws.Dispose();
                await DelayBackoffAsync(++attempt, ct).ConfigureAwait(false);
                continue;
            }

            // 연결 성공.
            _ws = ws;
            attempt = 0;
            var wasReconnect = _hasConnectedBefore;
            _hasConnectedBefore = true;
            SetState(ChatConnectionState.Connected);
            if (wasReconnect)
                Reconnected?.Invoke(this, EventArgs.Empty);   // 상위가 이력 재동기화 트리거

            try
            {
                await ReceivePumpAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* 정상 종료 */ }
            catch { /* 수신 오류 → 아래 재연결 로직으로 */ }
            finally
            {
                ws.Dispose();
                if (ReferenceEquals(_ws, ws)) _ws = null;
            }

            if (ct.IsCancellationRequested || _intentionalClose)
                break;

            // 비정상 끊김 → backoff 후 재연결.
            SetState(ChatConnectionState.Reconnecting);
            await DelayBackoffAsync(++attempt, ct).ConfigureAwait(false);
        }

        SetState(ChatConnectionState.Disconnected);
    }

    private async Task ReceivePumpAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var accum = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return; }   // 소켓 오류 → 연결 루프가 재연결

            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack close", ct).ConfigureAwait(false);
                }
                catch { /* ignore */ }
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;   // 바이너리 프레임 무시(메신저는 텍스트 JSON)

            accum.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
                continue;   // 멀티 프레임 누적

            var payload = accum.ToString();
            accum.Clear();
            DispatchInbound(payload);
        }
    }

    /// <summary>텍스트 프레임 → type peek → 해당 event raise(설계서 §2 디스패치 규칙). 미지 type/파싱 실패는 무시.</summary>
    private void DispatchInbound(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        string type;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return;
            type = typeEl.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return;   // 깨진 프레임 무시
        }

        try
        {
            switch (type)
            {
                case "message":
                    if (TryGetMessage(payload) is { } m) MessageReceived?.Invoke(this, m);
                    break;
                case "message_edited":
                    if (TryGetMessage(payload) is { } me) MessageEdited?.Invoke(this, me);
                    break;
                case "message_deleted":
                    if (TryGetMessage(payload) is { } md) MessageDeleted?.Invoke(this, md);
                    break;
                case "read":
                    if (Deserialize<WsReadEvent>(payload)?.Read is { } r) ReadReceived?.Invoke(this, r);
                    break;
                case "typing":
                    if (Deserialize<WsTypingEvent>(payload)?.Typing is { } t) TypingReceived?.Invoke(this, t);
                    break;
                case "member_joined":
                    if (Deserialize<WsMemberEvent>(payload)?.Member is { } mj) MemberJoined?.Invoke(this, mj);
                    break;
                case "member_left":
                    if (Deserialize<WsMemberEvent>(payload)?.Member is { } ml) MemberLeft?.Invoke(this, ml);
                    break;
                case "presence":
                    if (Deserialize<WsPresenceEvent>(payload)?.Presence is { } p) PresenceChanged?.Invoke(this, p);
                    break;
                case "error":
                    var err = Deserialize<WsErrorEvent>(payload)?.Error;
                    SocketError?.Invoke(this, err ?? "알 수 없는 소켓 오류");
                    break;
                // 미지 type(서버 ping 프레임 등)은 무시.
            }
        }
        catch (JsonException)
        {
            // payload 형태 불일치 → 무시.
        }
    }

    private static ChatMessage? TryGetMessage(string payload)
        => Deserialize<WsMessageEvent>(payload)?.Message;

    private static T? Deserialize<T>(string payload) where T : class
        => JsonSerializer.Deserialize<T>(payload, ChatJson.Options);

    // ─────────────────────────────────────────────────────────────────────────
    // 송신
    // ─────────────────────────────────────────────────────────────────────────

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException("WebSocket 이 연결되어 있지 않습니다.");

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 유틸
    // ─────────────────────────────────────────────────────────────────────────

    private Uri BuildWsUri()
    {
        var baseUrl = settings.Current.ServerBaseUrl?.TrimEnd('/') ?? "http://localhost:8080";
        // http→ws / https→wss 치환.
        string wsBase;
        if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            wsBase = "wss://" + baseUrl["https://".Length..];
        else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            wsBase = "ws://" + baseUrl["http://".Length..];
        else if (baseUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
                 baseUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            wsBase = baseUrl;
        else
            wsBase = "ws://" + baseUrl;   // 스킴 미상 → ws 기본

        return new Uri(wsBase + WsPath);
    }

    private async Task DelayBackoffAsync(int attempt, CancellationToken ct)
    {
        // 1→2→4→…최대 30s + 지터(±20%).
        var baseMs = MinBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 10));
        var cappedMs = Math.Min(baseMs, MaxBackoff.TotalMilliseconds);
        var jitterMs = cappedMs * (0.8 + _jitter.NextDouble() * 0.4);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(jitterMs), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 종료 신호 — 호출자가 루프 탈출 */ }
    }

    private static bool IsAuthRejection(WebSocketException wex)
    {
        // 핸드셰이크 HTTP 상태가 401/403 이면 메시지에 상태코드가 포함된다(BCL 한계 — 직접 상태 접근 불가).
        var msg = wex.Message;
        return msg.Contains("401", StringComparison.Ordinal) || msg.Contains("403", StringComparison.Ordinal);
    }

    private void SetState(ChatConnectionState next)
    {
        if (_state == next) return;
        _state = next;
        StateChanged?.Invoke(this, next);
    }
}
