using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class AgentApiClient(
    HttpClient httpClient,
    ISettingsService settings,
    HttpClient? restClient = null) : IAgentApiClient
{
    /// <summary>
    /// 컨트롤플레인(비스트리밍) 전용 클라이언트. 응답 압축(<c>Accept-Encoding</c>)을 켠 인스턴스를 주입받는다.
    ///
    /// 스트리밍(SSE)과 분리하는 이유: <c>AutomaticDecompression</c> 은 핸들러 단위 설정이라 요청별로 끌 수 없다
    /// (요청에 <c>Accept-Encoding: identity</c> 를 직접 넣어도 핸들러가 뒤에 gzip 을 덧붙인다 — 실측 확인).
    /// SSE 응답까지 압축을 광고하면 서버/프록시가 이벤트 스트림을 gzip 버퍼에 모았다 내보낼 수 있어
    /// 토큰이 실시간으로 흐르지 않는다. 그래서 채팅 스트림만은 압축을 광고하지 않는 클라이언트로 남긴다.
    ///
    /// 주입이 없으면 스트리밍 클라이언트를 그대로 쓴다(= 종전 동작).
    /// </summary>
    private readonly HttpClient _rest = restClient ?? httpClient;

    private const string ChatPath     = "/api/v1/agent/chat";
    private const string HealthPath   = "/api/v1/health";
    private const string ModelsPath   = "/api/v1/models";
    private const string LoginPath    = "/api/v1/auth/login";
    private const string ProfilePath  = "/api/v1/users/me";
    private const string ProjectsPath = "/api/v1/projects";
    private const string VersionPath  = "/api/v1/client/version";
    private const string QuotaPath    = "/api/v1/me/quota";
    private const string PolicyPath   = "/api/v1/tools/policy";
    private const string AuthorizePath = "/api/v1/tools/authorize";
    private const string CommandPolicyPath = "/api/v1/security/command-policy";
    private const string AgentSessionsPath = "/api/v1/agent/sessions";

    /// <summary>
    /// 요청 URI 를 매번 현재 설정의 ServerBaseUrl 기준으로 만든다.
    ///
    /// HttpClient.BaseAddress + 상대경로를 쓰지 않는 이유: BaseAddress 는 App 시작 시 한 번 설정되는데
    /// 첫 요청 이후에는 변경 자체가 InvalidOperationException 이다. 그래서 설정에서 서버 주소를 바꿔도
    /// 재시작 전까지 모든 요청이 옛 서버로 갔고, 아무 오류도 나지 않아 알아채기 어려웠다.
    /// 매 요청 절대 URI 를 만들면 BaseAddress 를 건드릴 필요가 없어 그 함정 자체가 사라진다.
    /// </summary>
    private Uri Url(string path)
    {
        var baseUrl = settings.Current.ServerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new AgentException("서버 주소가 설정되지 않았습니다. 설정에서 서버 주소를 입력하세요.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
            throw new AgentException($"서버 주소 형식이 올바르지 않습니다: {baseUrl}");

        return new Uri(root, path);
    }

    public async IAsyncEnumerable<AgentStreamEvent> SendAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(request, AgentJson.Options);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, Url(ChatPath))
        {
            Content = JsonBody(json)
        };
        httpReq.Headers.Accept.ParseAdd("text/event-stream");
        ApplyAuth(httpReq);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentException($"AI 서버에 연결할 수 없습니다: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errEvent = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                yield return errEvent;
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? eventName = null;
            var dataBuilder = new StringBuilder();

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }

                if (line is null)
                {
                    // 스트림 종료 — 버퍼에 미처리 이벤트가 있으면 마지막으로 dispatch.
                    var tail = Dispatch(eventName, dataBuilder.ToString());
                    if (tail is not null) yield return tail;
                    yield break;
                }

                if (line.Length == 0)
                {
                    // 이벤트 경계.
                    var evt = Dispatch(eventName, dataBuilder.ToString());
                    eventName = null;
                    dataBuilder.Clear();
                    if (evt is not null) yield return evt;
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                    dataBuilder.Append(line["data:".Length..].TrimStart());
                }
                // 그 외(주석 ':' 등)는 무시.
            }
        }
    }

    /// <summary>이 크기 미만이면 압축하지 않는다 — 줄어드는 양보다 gzip 헤더·CPU 가 아깝고, 작은 본문은 되레 커질 수 있다.</summary>
    private const int CompressRequestMinBytes = 32 * 1024;

    /// <summary>요청 본문 미디어 타입. 압축 여부와 무관하게 동일해야 하므로 한 곳에 둔다.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// JSON 요청 본문을 만든다. 설정(<see cref="AppSettings.CompressRequests"/>)이 켜져 있고 본문이
    /// <see cref="CompressRequestMinBytes"/> 이상이면 gzip 으로 압축해 <c>Content-Encoding: gzip</c> 을 붙인다.
    ///
    /// 기본이 꺼짐인 이유: 서버가 압축 본문을 해석하지 못하면 400/415 로 요청 자체가 실패한다.
    /// 서버 요구 스펙과 수용 기준은 docs/server-compression-spec.md 참고.
    ///
    /// 효과가 큰 이유: 에이전트는 도구 왕복마다 대화 전문을 다시 보내는데(ContextCompactor.BuildWireMessages),
    /// 그 안에 도구가 읽은 소스 코드 원문이 그대로 들어 있어 gzip 이 잘 듣는다(실측 200메시지 세션 78% 감소).
    /// 레벨 Optimal — 308KB 기준 Fastest 보다 1.5ms 더 쓰고 35KB 더 줄인다(실측).
    /// </summary>
    private HttpContent JsonBody(string json)
    {
        // 압축이 꺼져 있으면 바이트로 옮기는 것조차 하지 않는다(대부분의 요청이 이 경로).
        if (!settings.Current.CompressRequests)
            return PlainJson(json);

        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length < CompressRequestMinBytes)
            return PlainJson(json);

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(bytes);

        // ByteArrayContent — 리다이렉트·재전송 시 본문을 다시 읽을 수 있다(StreamContent 는 1회성이라 재전송에서 빈 본문이 된다).
        var content = new ByteArrayContent(ms.GetBuffer(), 0, (int)ms.Length);
        content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType) { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    private static StringContent PlainJson(string json) => new(json, Encoding.UTF8, JsonMediaType);

    // 컨트롤플레인(비스트리밍) 요청 상한 — SSE용 무한 타임아웃 공유 HttpClient가 무응답 서버에서
    // 영구 대기(로그인/시작 화면 정지)하지 않도록 요청별 타임아웃을 건다. 타임아웃은 연결 실패로 취급된다.
    private const int ControlPlaneTimeoutSeconds = 30;

    // 모든 비스트리밍 REST 호출이 이 한 곳을 지난다 — 응답 압축은 여기서만 켜진다(_rest 주석 참조).
    private async Task<HttpResponseMessage> SendControlAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ControlPlaneTimeoutSeconds));
        return await _rest.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 서버 도달 여부만 본다 — 화면의 "서버 연결됨" 표시 전용.
    ///
    /// 의도적으로 (1) 토큰을 붙이지 않고 (2) 상태 코드를 따지지 않는다.
    /// /health 는 Public 이므로 인증은 불필요하고, 만료된 토큰이나 서버 내부 오류(5xx)로
    /// "연결 안됨" 이 뜨면 사용자는 서버가 죽은 줄 오해한다. 응답이 왔다는 것 자체가 연결의 증거다.
    /// 인증 여부 판정은 <see cref="CheckReadinessAsync"/> 의 몫이다.
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(HealthPath));
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            return true;   // 상태 코드 불문 — 응답이 왔으면 연결된 것.
        }
        catch
        {
            return false;  // 전송 실패/타임아웃만 미연결.
        }
    }

    public async Task<ServerReadiness> CheckReadinessAsync(CancellationToken ct = default)
    {
        // 1) 연결성: Public /health.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(HealthPath));
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return ServerReadiness.Disconnected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return ServerReadiness.Disconnected; }

        // 2) 토큰 부재 → 로그인 필요.
        if (string.IsNullOrWhiteSpace(settings.Current.AuthToken))
            return ServerReadiness.Unauthenticated;

        // 3) 토큰 유효성: 보호된 엔드포인트(/models)가 401/403 이면 만료/무효.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(ModelsPath));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            return resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? ServerReadiness.Unauthenticated
                : ServerReadiness.Ready;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // 연결성은 이미 입증됨 — 일시 오류로 로그인 상태를 단정하지 않는다.
            return ServerReadiness.Ready;
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(ModelsPath));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<ModelInfo>();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("models", out var modelsEl) &&
                modelsEl.ValueKind == JsonValueKind.Array)
            {
                var models = modelsEl.Deserialize<List<ModelInfo>>(AgentJson.Options);
                return models ?? new List<ModelInfo>();
            }

            return Array.Empty<ModelInfo>();
        }
        catch
        {
            return Array.Empty<ModelInfo>();
        }
    }

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new LoginRequestDto(username, password), AgentJson.Options);

            // Public 엔드포인트 — 토큰 발급 전이므로 ApplyAuth 미부착.
            using var req = new HttpRequestMessage(HttpMethod.Post, Url(LoginPath))
            {
                Content = JsonBody(payload)
            };

            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
                return LoginResult.Fail(err.Message);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer
                .DeserializeAsync<LoginResponseDto>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(dto?.Token)
                ? LoginResult.Fail("로그인 응답에 토큰이 없습니다.")
                : LoginResult.Ok(dto!.Token!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LoginResult.Fail($"AI 서버에 연결할 수 없습니다: {ex.Message}");
        }
    }

    /// <summary>
    /// Bearer GET → <typeparamref name="T"/> 역직렬화 공용 헬퍼. 비2xx(404 미구현/401/5xx)·오프라인·파싱 실패는
    /// graceful null, 취소는 전파. 단순 GET 엔드포인트(프로필/버전/쿼터/도구정책/명령정책)가 공유한다.
    /// 응답 envelope 처리가 다른 엔드포인트(models/projects 등)는 사용하지 않는다.
    /// </summary>
    private async Task<T?> GetGracefulAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, AgentJson.Options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public Task<UserProfile?> GetProfileAsync(CancellationToken ct = default)
        => GetGracefulAsync<UserProfile>(ProfilePath, ct);

    public Task<ClientVersionInfo?> GetClientVersionAsync(CancellationToken ct = default)
        => GetGracefulAsync<ClientVersionInfo>(VersionPath, ct);

    public Task<QuotaResponse?> GetQuotaAsync(CancellationToken ct = default)
        => GetGracefulAsync<QuotaResponse>(QuotaPath, ct);

    /// <summary>
    /// 도구 정책 조회. <see cref="GetGracefulAsync{T}"/> 를 쓰지 않는다 — 그 헬퍼는 404와 5xx/오프라인을
    /// 똑같이 null 로 뭉개는데, 정책 게이트에서는 그 구분이 곧 보안 경계이기 때문이다(404=정책 없음→허용,
    /// 그 외 실패=판단 불가→차단). 나머지 graceful GET 엔드포인트는 이 구분이 필요 없다.
    /// </summary>
    public async Task<ToolPolicyFetch> GetToolPolicyAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(PolicyPath));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return ToolPolicyFetch.NotImplemented;

            if (!resp.IsSuccessStatusCode)
                return ToolPolicyFetch.Failed;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var policy = await JsonSerializer.DeserializeAsync<ToolPolicy>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);

            return policy is null ? ToolPolicyFetch.Failed : ToolPolicyFetch.Ok(policy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ToolPolicyFetch.Failed;
        }
    }

    public Task<CommandSecurityPolicyResponse?> GetCommandSecurityPolicyAsync(CancellationToken ct = default)
        => GetGracefulAsync<CommandSecurityPolicyResponse>(CommandPolicyPath, ct);

    public async Task<ToolAuthorization?> AuthorizeToolAsync(string tool, JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new AuthorizeRequestDto(tool, arguments), AgentJson.Options);

            using var req = new HttpRequestMessage(HttpMethod.Post, Url(AuthorizePath))
            {
                Content = JsonBody(payload)
            };
            ApplyAuth(req);

            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;   // 오류 → graceful null(호출자가 fail-closed 처리)

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer
                .DeserializeAsync<ToolAuthorization>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;   // 오프라인/파싱 실패 → graceful null
        }
    }

    public async Task<IReadOnlyList<RemoteProject>> ListRemoteProjectsAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(ProjectsPath));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<RemoteProject>();   // 404/501 등 미지원 → 빈 목록

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            // {"projects":[...]} 또는 평면 배열 둘 다 수용.
            JsonElement arrayEl;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arrayEl = doc.RootElement;
            else if (doc.RootElement.TryGetProperty("projects", out var p) && p.ValueKind == JsonValueKind.Array)
                arrayEl = p;
            else
                return Array.Empty<RemoteProject>();

            var list = arrayEl.Deserialize<List<RemoteProject>>(AgentJson.Options);
            return list ?? new List<RemoteProject>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<RemoteProject>();
        }
    }

    public async Task<RemoteProject> UpsertRemoteProjectAsync(RemoteProjectUpsert body, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(body, AgentJson.Options);
            using var req = new HttpRequestMessage(HttpMethod.Post, Url(ProjectsPath))
            {
                Content = JsonBody(payload)
            };
            ApplyAuth(req);

            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
                throw new AgentException($"프로젝트 동기화 실패: {err.Message}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer
                .DeserializeAsync<RemoteProject>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);
            return dto ?? throw new AgentException("프로젝트 동기화 응답이 비었습니다.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentException($"AI 서버에 연결할 수 없습니다: {ex.Message}", ex);
        }
    }

    public async Task UpsertRemoteConversationAsync(string remoteProjectId, RemoteConversation body, CancellationToken ct = default)
    {
        try
        {
            var path    = $"{ProjectsPath}/{Uri.EscapeDataString(remoteProjectId)}/conversations";
            var payload = JsonSerializer.Serialize(body, AgentJson.Options);
            using var req = new HttpRequestMessage(HttpMethod.Post, Url(path))
            {
                Content = JsonBody(payload)
            };
            ApplyAuth(req);

            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
                throw new AgentException($"대화 동기화 실패: {err.Message}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentException($"AI 서버에 연결할 수 없습니다: {ex.Message}", ex);
        }
    }

    public async Task DeleteRemoteProjectAsync(string remoteProjectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteProjectId))
            return;   // 원격 미동기화(remote_id 없음) → no-op

        try
        {
            var path = $"{ProjectsPath}/{Uri.EscapeDataString(remoteProjectId)}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, Url(path));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            // 204/200/404(이미 없음) 모두 성공으로 간주 — 삭제는 멱등. 그 외 상태도 graceful no-op.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 오프라인/미지원 → graceful no-op(로컬 삭제는 호출자가 이미 수행).
        }
    }

    public async Task DeleteRemoteConversationAsync(string remoteProjectId, string remoteConversationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteProjectId) || string.IsNullOrWhiteSpace(remoteConversationId))
            return;   // 원격 미동기화 → no-op

        try
        {
            var path = $"{ProjectsPath}/{Uri.EscapeDataString(remoteProjectId)}/conversations/{Uri.EscapeDataString(remoteConversationId)}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, Url(path));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            // 멱등 삭제 — 404 포함 graceful.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 오프라인/미지원 → graceful no-op.
        }
    }

    public async Task<IReadOnlyList<RemoteSessionSummary>?> ListRemoteSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(AgentSessionsPath));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;   // 404/501/오류 → graceful null

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer
                .DeserializeAsync<RemoteSessionList>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);
            return dto?.Sessions;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;   // 오프라인/파싱 실패 → graceful
        }
    }

    public async Task<RemoteSession?> GetRemoteSessionAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        try
        {
            var path = $"{AgentSessionsPath}/{Uri.EscapeDataString(id)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;   // 404/오류 → graceful null

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer
                .DeserializeAsync<RemoteSession>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;   // 오프라인/파싱 실패 → graceful
        }
    }

    public async Task<bool> PutRemoteSessionAsync(string id, string title, JsonElement data, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        try
        {
            var path    = $"{AgentSessionsPath}/{Uri.EscapeDataString(id)}";
            var payload = JsonSerializer.Serialize(new SessionUpsertDto(title, data), AgentJson.Options);
            using var req = new HttpRequestMessage(HttpMethod.Put, Url(path))
            {
                Content = JsonBody(payload)
            };
            ApplyAuth(req);

            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;   // 403/오류 → false(예외 없음)
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;   // 오프라인/오류 → graceful false
        }
    }

    public async Task DeleteRemoteSessionAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;   // no-op

        try
        {
            var path = $"{AgentSessionsPath}/{Uri.EscapeDataString(id)}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, Url(path));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            // 204/200/404(이미 없음) 모두 성공으로 간주 — 삭제는 멱등. 그 외도 graceful no-op.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 오프라인/미지원 → graceful no-op.
        }
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        var token = settings.Current.AuthToken;
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>SSE 한 이벤트를 AgentStreamEvent 로 매핑. 데이터 없으면 null.</summary>
    private static AgentStreamEvent? Dispatch(string? eventName, string data)
    {
        if (string.IsNullOrEmpty(eventName) || string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            switch (eventName)
            {
                case "message_start":
                    return new MessageStart(
                        GetString(root, "id"),
                        GetString(root, "model"));

                case "content_delta":
                    return new ContentDelta(GetString(root, "delta"));

                case "thinking_delta":
                    return new ThinkingDelta(GetString(root, "delta"));

                case "tool_call":
                    JsonElement args;
                    if (root.TryGetProperty("arguments", out var argsEl))
                    {
                        if (argsEl.ValueKind == JsonValueKind.String)
                        {
                            // 서버는 arguments 를 JSON 문자열로 전달 → 한 번 더 파싱해 객체화.
                            var raw = argsEl.GetString();
                            if (string.IsNullOrWhiteSpace(raw))
                            {
                                args = EmptyObject();
                            }
                            else
                            {
                                try
                                {
                                    using var inner = JsonDocument.Parse(raw);
                                    args = inner.RootElement.Clone();
                                }
                                catch (JsonException)
                                {
                                    args = EmptyObject();
                                }
                            }
                        }
                        else
                        {
                            // 이미 객체/배열인 경우 방어적 통과.
                            args = argsEl.Clone();
                        }
                    }
                    else
                    {
                        args = EmptyObject();
                    }

                    return new ToolCallEvent(
                        GetString(root, "id"),
                        GetString(root, "name"),
                        args);

                case "message_stop":
                    var stopReason = GetString(root, "stop_reason");
                    var usage = root.TryGetProperty("usage", out var usageEl)
                        ? usageEl.Deserialize<Usage>(AgentJson.Options) ?? new Usage(0, 0)
                        : new Usage(0, 0);
                    return new MessageStop(stopReason, usage,
                        GetString(root, "thinking"), GetString(root, "thinking_signature"));

                case "error":
                    // 서버는 스트리밍 error 를 중첩 envelope 로 전달: {"error":{"code","message"}}.
                    // 평면 형태({"code","message"})도 방어적으로 허용.
                    var errBody = root.TryGetProperty("error", out var errEl) ? errEl : root;
                    return new ErrorEvent(
                        GetString(errBody, "code"),
                        GetString(errBody, "message"));

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            // 깨진 data 라인은 무시.
            return null;
        }
    }

    private static async Task<ErrorEvent> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = $"http_{(int)response.StatusCode}";
        var message = response.ReasonPhrase ?? "요청 실패";

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                // 에이전트 계약 API(health/models/agent/*)는 중첩 {"error":{code,message}},
                // 관리 API(auth/login 등)는 평면 {code,message} envelope 를 쓴다(서버 §5.2). 둘 다 수용.
                var errBody = doc.RootElement.TryGetProperty("error", out var errEl)
                    ? errEl
                    : doc.RootElement;
                code = GetString(errBody, "code", code);
                message = GetString(errBody, "message", message);
            }
        }
        catch
        {
            // 파싱 실패 시 HTTP 상태 기반 기본값 유지.
        }

        return new ErrorEvent(code, message);
    }

    private static string GetString(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private sealed record LoginRequestDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("username")] string Username,
        [property: System.Text.Json.Serialization.JsonPropertyName("password")] string Password);

    private sealed record LoginResponseDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("token")] string? Token);

    private sealed record AuthorizeRequestDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("tool")]      string Tool,
        [property: System.Text.Json.Serialization.JsonPropertyName("arguments")] JsonElement Arguments);

    private sealed record SessionUpsertDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
        [property: System.Text.Json.Serialization.JsonPropertyName("data")]  JsonElement Data);
}
