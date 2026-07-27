using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Chat;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// Chat REST 클라이언트. AgentApiClient 헬퍼 패턴 미러(ApplyAuth/ReadErrorAsync/const Path/ChatJson.Options).
/// App 의 메인 <see cref="HttpClient"/>(BaseAddress 없음)를 공유 주입받아, 요청마다
/// 설정의 ServerBaseUrl 로 절대 URI 를 만든다(<see cref="Url"/>).
/// 에러는 <see cref="ChatApiException"/>(상태코드 보존)로 throw → 호출자가 401 분기.
/// </summary>
public sealed class ChatApiClient(HttpClient httpClient, ISettingsService settings) : IChatApiClient
{
    private const string RoomsPath       = "/api/v1/chat/rooms";
    private const string DirectRoomPath  = "/api/v1/chat/rooms/direct";
    private const string UnreadPath      = "/api/v1/chat/unread";
    private const string MentionsPath    = "/api/v1/chat/mentions";
    private const string AttachmentsPath = "/api/v1/chat/attachments";

    private const string ProfilePath = "/api/v1/users/me";
    private const string MembersPath = "/api/v1/members";        // admin 전용 — 비admin 은 403(graceful)

    private const long MaxAttachmentBytes = 10 * 1024 * 1024;   // 10 MiB

    /// <summary>REST 요청 1건의 상한(초). 공유 HttpClient 는 Timeout.InfiniteTimeSpan 이라 여기서 걸어야 한다.</summary>
    private const int RequestTimeoutSeconds = 30;

    /// <summary>
    /// 요청별 타임아웃을 건 전송. 주입받는 HttpClient 는 무한 타임아웃(SSE 스트리밍용)이므로,
    /// 이걸 걸지 않으면 서버가 TCP 는 받고 응답하지 않는 상태(절전/VPN 끊김 후 반열림 소켓)에서
    /// 메신저가 "연결 중" 으로 영원히 멈춘다. AgentApiClient.SendControlAsync 와 같은 규약.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
        return await httpClient.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 요청 URI 를 매번 현재 설정의 ServerBaseUrl 기준 절대 URI 로 만든다(AgentApiClient.Url 과 동일 규약).
    ///
    /// 주입받는 HttpClient 에는 BaseAddress 가 없다(App.xaml.cs — 첫 요청 후 변경 불가 함정 회피).
    /// 여기서 상대 경로를 그대로 넘기면 HttpClient 가 InvalidOperationException 을 던지고,
    /// 그것이 blanket catch 에서 "network_error" 로 뭉개져 서버 다운과 구분되지 않는다.
    /// </summary>
    private Uri Url(string path)
    {
        var baseUrl = settings.Current.ServerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ChatApiException(0, "config_error", "서버 주소가 설정되지 않았습니다. 설정에서 서버 주소를 입력하세요.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
            throw new ChatApiException(0, "config_error", $"서버 주소 형식이 올바르지 않습니다: {baseUrl}");

        return new Uri(root, path);
    }

    // ── 이름 디렉터리(memberId → 표시이름) 소스 ──

    /// <summary>본인 프로필(/users/me). 표시이름 해석용. 실패 시 null(graceful).</summary>
    public async Task<UserProfile?> GetMyProfileAsync(CancellationToken ct = default)
    {
        try { return await GetAsync<UserProfile>(ProfilePath, ct).ConfigureAwait(false); }
        catch (ChatApiException) { return null; }
    }

    /// <summary>
    /// 멤버 디렉터리(id → 이름) best-effort. 현재 소스는 admin 전용 <c>/members</c> 라 일반 user 는 403→null.
    /// 서버가 멤버 스코프 이름 엔드포인트를 추가하면 이 메서드 소스만 교체한다.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> TryGetMemberDirectoryAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await GetAsync<MembersListResponse>($"{MembersPath}?limit=200", ct).ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in resp.Items ?? Array.Empty<MemberItem>())
            {
                var name = !string.IsNullOrWhiteSpace(m.DisplayName) ? m.DisplayName!
                         : !string.IsNullOrWhiteSpace(m.Username) ? m.Username! : null;
                if (name is not null && !string.IsNullOrEmpty(m.Id)) map[m.Id] = name;
            }
            return map;
        }
        catch (ChatApiException) { return null; }   // 403(비admin)/기타 → 디렉터리 없음(폴백)
    }

    private sealed record MembersListResponse(
        [property: JsonPropertyName("items")] IReadOnlyList<MemberItem>? Items);

    private sealed record MemberItem(
        [property: JsonPropertyName("id")]           string Id,
        [property: JsonPropertyName("username")]     string? Username,
        [property: JsonPropertyName("display_name")] string? DisplayName);

    // ── 방 ──

    public async Task<IReadOnlyList<ChatRoom>> GetRoomsAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync<ChatRoomsResponse>(RoomsPath, ct).ConfigureAwait(false);
        return resp.Rooms ?? Array.Empty<ChatRoom>();
    }

    public Task<ChatRoom> CreateGroupRoomAsync(string name, IReadOnlyList<string> memberIds, CancellationToken ct = default)
        => PostAsync<ChatRoom>(RoomsPath, new CreateGroupRoomRequest(name, memberIds), ct);

    public Task<ChatRoom> GetOrCreateDirectRoomAsync(string userId, CancellationToken ct = default)
        => PostAsync<ChatRoom>(DirectRoomPath, new CreateDirectRoomRequest(userId), ct);

    // ── 메시지 ──

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string roomId, int limit = 50, string? before = null, CancellationToken ct = default)
    {
        // ⚠️ 서버 한계: 이 응답에는 mentions/attachments 가 누락된다(WS message DTO 에만 존재).
        //    역직렬화 시 해당 필드는 null → UI 에서 미표시. 실시간 수신분(WS)만 멘션/첨부 렌더(SSOT 임시 대응).
        var query = new StringBuilder($"{RoomsPath}/{Uri.EscapeDataString(roomId)}/messages?limit={limit}");
        if (!string.IsNullOrWhiteSpace(before))
            query.Append("&before=").Append(Uri.EscapeDataString(before));

        var resp = await GetAsync<ChatMessagesResponse>(query.ToString(), ct).ConfigureAwait(false);
        return resp.Messages ?? Array.Empty<ChatMessage>();
    }

    public Task<ChatMessage> SendMessageAsync(string roomId, SendMessageRequest body, CancellationToken ct = default)
        => PostAsync<ChatMessage>($"{RoomsPath}/{Uri.EscapeDataString(roomId)}/messages", body, ct);

    public Task<ChatMessage> EditMessageAsync(string roomId, string messageId, string content, CancellationToken ct = default)
        => SendAsync<ChatMessage>(
            HttpMethod.Patch,
            $"{RoomsPath}/{Uri.EscapeDataString(roomId)}/messages/{Uri.EscapeDataString(messageId)}",
            new EditMessageRequest(content), ct);

    public Task DeleteMessageAsync(string roomId, string messageId, CancellationToken ct = default)
        => SendNoContentAsync(
            HttpMethod.Delete,
            $"{RoomsPath}/{Uri.EscapeDataString(roomId)}/messages/{Uri.EscapeDataString(messageId)}",
            body: null, ct);

    // ── 읽음 ──

    public Task<ChatReadResult> MarkReadAsync(string roomId, CancellationToken ct = default)
        => PostAsync<ChatReadResult>($"{RoomsPath}/{Uri.EscapeDataString(roomId)}/read", body: null, ct);

    public async Task<IReadOnlyList<ChatReadReceipt>> GetReadsAsync(string roomId, CancellationToken ct = default)
    {
        var resp = await GetAsync<ChatReadsResponse>($"{RoomsPath}/{Uri.EscapeDataString(roomId)}/reads", ct).ConfigureAwait(false);
        return resp.Reads ?? Array.Empty<ChatReadReceipt>();
    }

    public Task<ChatUnreadResponse> GetUnreadAsync(CancellationToken ct = default)
        => GetAsync<ChatUnreadResponse>(UnreadPath, ct);

    // ── 멤버 / presence ──

    public async Task<IReadOnlyList<string>> GetMembersAsync(string roomId, CancellationToken ct = default)
    {
        var resp = await GetAsync<ChatMembersResponse>($"{RoomsPath}/{Uri.EscapeDataString(roomId)}/members", ct).ConfigureAwait(false);
        return resp.Members ?? Array.Empty<string>();
    }

    public async Task<IReadOnlyList<ChatMemberDetail>> GetRoomMembersDetailedAsync(string roomId, CancellationToken ct = default)
    {
        // ?detail=1 → 이름(username/display_name) 포함. 방 멤버라면 누구나(admin 불필요).
        var resp = await GetAsync<ChatMembersDetailResponse>($"{RoomsPath}/{Uri.EscapeDataString(roomId)}/members?detail=1", ct).ConfigureAwait(false);
        return resp.Members ?? Array.Empty<ChatMemberDetail>();
    }

    public async Task<IReadOnlyList<string>> AddMembersAsync(string roomId, IReadOnlyList<string> memberIds, CancellationToken ct = default)
    {
        var resp = await PostAsync<ChatMembersResponse>(
            $"{RoomsPath}/{Uri.EscapeDataString(roomId)}/members",
            new AddMembersRequest(memberIds), ct).ConfigureAwait(false);
        return resp.Members ?? Array.Empty<string>();
    }

    public Task KickMemberAsync(string roomId, string memberId, CancellationToken ct = default)
        => SendNoContentAsync(
            HttpMethod.Delete,
            $"{RoomsPath}/{Uri.EscapeDataString(roomId)}/members/{Uri.EscapeDataString(memberId)}",
            body: null, ct);

    public Task LeaveRoomAsync(string roomId, CancellationToken ct = default)
        => SendNoContentAsync(
            HttpMethod.Post,
            $"{RoomsPath}/{Uri.EscapeDataString(roomId)}/leave",
            body: null, ct);

    public async Task<IReadOnlyList<string>> GetPresenceAsync(string roomId, CancellationToken ct = default)
    {
        var resp = await GetAsync<ChatPresenceResponse>($"{RoomsPath}/{Uri.EscapeDataString(roomId)}/presence", ct).ConfigureAwait(false);
        return resp.Online ?? Array.Empty<string>();
    }

    // ── 멘션 / 첨부 ──

    public async Task<IReadOnlyList<ChatMessage>> GetMentionsAsync(int limit = 50, CancellationToken ct = default)
    {
        var resp = await GetAsync<ChatMessagesResponse>($"{MentionsPath}?limit={limit}", ct).ConfigureAwait(false);
        return resp.Messages ?? Array.Empty<ChatMessage>();
    }

    public async Task<ChatAttachment> UploadAttachmentAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new ChatApiException(400, "invalid_file", "업로드할 파일을 찾을 수 없습니다.");

        var info = new FileInfo(filePath);
        if (info.Length > MaxAttachmentBytes)
            throw new ChatApiException(400, "file_too_large", "첨부 파일은 10MiB 를 초과할 수 없습니다.");

        try
        {
            await using var fileStream = File.OpenRead(filePath);
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, "file", info.Name);   // part 명 = "file"

            using var req = new HttpRequestMessage(HttpMethod.Post, Url(AttachmentsPath)) { Content = content };
            ApplyAuth(req);

            using var resp = await SendWithTimeoutAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw await CreateExceptionAsync(resp, ct).ConfigureAwait(false);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<ChatAttachment>(stream, ChatJson.Options, ct).ConfigureAwait(false);
            return dto ?? throw new ChatApiException((int)resp.StatusCode, "empty_response", "첨부 업로드 응답이 비었습니다.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChatApiException) { throw; }
        catch (Exception ex)
        {
            throw new ChatApiException(0, "network_error", $"첨부 업로드 실패: {ex.Message}", ex);
        }
    }

    public async Task<Stream> DownloadAttachmentAsync(string attachmentId, CancellationToken ct = default)
    {
        try
        {
            var path = $"{AttachmentsPath}/{Uri.EscapeDataString(attachmentId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
            ApplyAuth(req);

            // 응답 헤더만 먼저 받고 바이너리 스트림은 호출자가 소비/Dispose 한다.
            var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                using (resp)
                    throw await CreateExceptionAsync(resp, ct).ConfigureAwait(false);
            }

            // StreamWithResponse: 스트림 Dispose 시 응답도 함께 정리.
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ResponseBackedStream(stream, resp);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChatApiException) { throw; }
        catch (Exception ex)
        {
            throw new ChatApiException(0, "network_error", $"첨부 다운로드 실패: {ex.Message}", ex);
        }
    }

    // ── 공용 HTTP 헬퍼 (AgentApiClient 패턴 미러) ────────────────────────

    private Task<T> GetAsync<T>(string path, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Get, path, body: null, ct);

    private Task<T> PostAsync<T>(string path, object? body, CancellationToken ct)
        => SendAsync<T>(HttpMethod.Post, path, body, ct);

    /// <summary>JSON 요청 → 2xx 검증 → <typeparamref name="T"/> 역직렬화. 비2xx 는 <see cref="ChatApiException"/>(상태코드 보존).</summary>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var req = BuildJsonRequest(method, path, body);
            using var resp = await SendWithTimeoutAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw await CreateExceptionAsync(resp, ct).ConfigureAwait(false);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<T>(stream, ChatJson.Options, ct).ConfigureAwait(false);
            return dto ?? throw new ChatApiException((int)resp.StatusCode, "empty_response", "서버 응답이 비었습니다.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChatApiException) { throw; }
        catch (Exception ex)
        {
            throw new ChatApiException(0, "network_error", $"채팅 서버에 연결할 수 없습니다: {ex.Message}", ex);
        }
    }

    /// <summary>본문 무시(204/멱등) 요청. 비2xx 는 throw — 단 404(이미 없음)는 멱등 삭제로 graceful 통과.</summary>
    private async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var req = BuildJsonRequest(method, path, body);
            using var resp = await SendWithTimeoutAsync(req, ct).ConfigureAwait(false);

            // DELETE 멱등: 404(이미 없음)는 성공으로 간주.
            if (method == HttpMethod.Delete && resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return;

            if (!resp.IsSuccessStatusCode)
                throw await CreateExceptionAsync(resp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChatApiException) { throw; }
        catch (Exception ex)
        {
            throw new ChatApiException(0, "network_error", $"채팅 서버에 연결할 수 없습니다: {ex.Message}", ex);
        }
    }

    private HttpRequestMessage BuildJsonRequest(HttpMethod method, string path, object? body)
    {
        var req = new HttpRequestMessage(method, Url(path));
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, ChatJson.Options);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        ApplyAuth(req);
        return req;
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        var token = settings.Current.AuthToken;
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// 비2xx 응답 → <see cref="ChatApiException"/>. 중첩 envelope `{"error":{"code","message"}}` 우선,
    /// 평면 `{"code","message"}` 도 방어적 허용(AgentApiClient.ReadErrorAsync 미러).
    /// </summary>
    private static async Task<ChatApiException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status  = (int)response.StatusCode;
        var code    = $"http_{status}";
        var message = response.ReasonPhrase ?? "요청 실패";

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var errBody = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl : doc.RootElement;
                code    = GetString(errBody, "code", code);
                message = GetString(errBody, "message", message);
            }
        }
        catch
        {
            // 파싱 실패 시 HTTP 상태 기반 기본값 유지.
        }

        return new ChatApiException(status, code, message);
    }

    private static string GetString(JsonElement el, string prop, string fallback)
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    /// <summary>다운로드 스트림 Dispose 시 원본 HttpResponseMessage 도 함께 정리하는 래퍼.</summary>
    private sealed class ResponseBackedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
