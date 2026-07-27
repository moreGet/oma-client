using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// <see cref="IAgentRegistryClient"/> 구현. AgentApiClient 형제 — 매 요청 절대 URI(Url) + Bearer(ApplyAuth)
/// 패턴을 복제한다(private 재사용 불가 → 의도적 5줄 중복). 레지스트리는 컨트롤플레인이므로 SSE용 무한
/// 타임아웃 공유 HttpClient 에 요청별 30s 상한(SendControlAsync)을 건다(무응답 서버 무한 대기 방지).
/// </summary>
public sealed class AgentRegistryClient(HttpClient httpClient, ISettingsService settings) : IAgentRegistryClient
{
    private const string RegisterPath  = "/api/v1/agents/register";
    private const string AgentsPath    = "/api/v1/agents";
    private const string PublicKeyPath = "/api/v1/agents/a2a-public-key";

    private const int ControlPlaneTimeoutSeconds = 30;

    private Uri Url(string path)
    {
        var baseUrl = settings.Current.ServerBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new AgentException("서버 주소가 설정되지 않았습니다. 설정에서 서버 주소를 입력하세요.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
            throw new AgentException($"서버 주소 형식이 올바르지 않습니다: {baseUrl}");

        return new Uri(root, path);
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        var token = settings.Current.AuthToken;
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<HttpResponseMessage> SendControlAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ControlPlaneTimeoutSeconds));
        return await httpClient.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
    }

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(req, AgentJson.Options);
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, Url(RegisterPath))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            ApplyAuth(httpReq);

            using var resp = await SendControlAsync(httpReq, ct).ConfigureAwait(false);

            // 401/403 = 자격증명 사망. 재시도해도 영원히 실패하므로 typed 신호로 구분한다(호출자가 종료 판단).
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AgentUnauthorizedException("agents/register", (int)resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                var (code, message) = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
                throw new AgentException($"에이전트 등록 실패: {message} ({code})");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer
                .DeserializeAsync<RegisterResponse>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);
            return dto ?? throw new AgentException("에이전트 등록 응답이 비었습니다.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentUnauthorizedException)
        {
            throw;   // 자격증명 사망 — 일반 연결 오류로 뭉개면 호출자가 종료 판단을 못 한다.
        }
        catch (AgentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentException($"레지스트리에 연결할 수 없습니다: {ex.Message}", ex);
        }
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(string agentId, CancellationToken ct = default)
    {
        try
        {
            var path = $"{AgentsPath}/{Uri.EscapeDataString(agentId)}/heartbeat";
            using var req = new HttpRequestMessage(HttpMethod.Post, Url(path));
            ApplyAuth(req);

            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);

            // 404 = 소유자 아님/리스 만료 정리 → typed 신호(lifecycle 가 재-register).
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new AgentLeaseExpiredException(agentId);

            // 401/403 = 토큰 사망. 404 와 달리 재등록으로 치유되지 않으므로 별도 신호(lifecycle 가 종료 판단).
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AgentUnauthorizedException("agents/heartbeat", (int)resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                var (code, message) = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
                throw new AgentException($"heartbeat 실패: {message} ({code})");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer
                .DeserializeAsync<HeartbeatResponse>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);
            return dto ?? throw new AgentException("heartbeat 응답이 비었습니다.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentLeaseExpiredException)
        {
            throw;
        }
        catch (AgentUnauthorizedException)
        {
            throw;   // 404(재등록으로 치유)와 달리 종료가 정답 — 일반 오류로 뭉개지 않는다.
        }
        catch (AgentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentException($"레지스트리에 연결할 수 없습니다: {ex.Message}", ex);
        }
    }

    public async Task DeregisterAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return;

        try
        {
            var path = $"{AgentsPath}/{Uri.EscapeDataString(agentId)}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, Url(path));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            // 204/200/404(이미 없음) 모두 성공 간주 — 해제는 멱등. 그 외도 graceful no-op.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 오프라인/미지원 → graceful no-op(best-effort 해제).
        }
    }

    public async Task<IReadOnlyList<AgentDescriptor>> DiscoverAsync(DiscoverQuery query, CancellationToken ct = default)
    {
        try
        {
            var path = AgentsPath + BuildQueryString(query);
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<AgentDescriptor>();   // 비2xx/미지원 → 후보 없음

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            // {agents:[...]} envelope 우선, 평면 배열도 방어 수용(ListRemoteProjects 관례).
            JsonElement arrayEl;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arrayEl = doc.RootElement;
            else if (doc.RootElement.TryGetProperty("agents", out var a) && a.ValueKind == JsonValueKind.Array)
                arrayEl = a;
            else
                return Array.Empty<AgentDescriptor>();

            var list = arrayEl.Deserialize<List<AgentDescriptor>>(AgentJson.Options);
            return list ?? new List<AgentDescriptor>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<AgentDescriptor>();   // 오프라인/파싱 실패 → 후보 없음
        }
    }

    public async Task<AgentDescriptor?> GetAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return null;

        try
        {
            var path = $"{AgentsPath}/{Uri.EscapeDataString(agentId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;   // 404/비2xx → graceful null

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer
                .DeserializeAsync<AgentDescriptor>(stream, AgentJson.Options, ct)
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

    public async Task<A2aToken> MintA2aTokenAsync(string targetAgentId, CancellationToken ct = default)
    {
        try
        {
            var path = $"{AgentsPath}/{Uri.EscapeDataString(targetAgentId)}/token";
            using var req = new HttpRequestMessage(HttpMethod.Post, Url(path));   // 본문 없음
            ApplyAuth(req);

            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new AgentException($"A2A 토큰 발급 실패: 대상 에이전트가 존재하지 않습니다({targetAgentId}).");

            if (!resp.IsSuccessStatusCode)
            {
                var (code, message) = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
                throw new AgentException($"A2A 토큰 발급 실패: {message} ({code})");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer
                .DeserializeAsync<A2aToken>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);
            return dto ?? throw new AgentException("A2A 토큰 응답이 비었습니다.");
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
            throw new AgentException($"레지스트리에 연결할 수 없습니다: {ex.Message}", ex);
        }
    }

    public async Task<A2aPublicKey> GetA2aPublicKeyAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url(PublicKeyPath));
            ApplyAuth(req);
            using var resp = await SendControlAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var (code, message) = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
                throw new AgentException($"공개키 취득 실패: {message} ({code})");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer
                .DeserializeAsync<A2aPublicKey>(stream, AgentJson.Options, ct)
                .ConfigureAwait(false);
            return dto ?? throw new AgentException("공개키 응답이 비었습니다.");
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
            throw new AgentException($"레지스트리에 연결할 수 없습니다: {ex.Message}", ex);
        }
    }

    /// <summary>DiscoverQuery → ?capability=&amp;tag=&amp;status=&amp;q=&amp;exclude_self= (null/빈값은 생략).</summary>
    private static string BuildQueryString(DiscoverQuery q)
    {
        var parts = new List<string>(5);
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        Add("capability", q.Capability);
        Add("tag", q.Tag);
        Add("status", q.Status);
        Add("q", q.Query);
        Add("exclude_self", q.ExcludeSelf);

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    /// <summary>오류 응답 → (code, message). 중첩 {"error":{code,message}} 및 평면 둘 다 수용.</summary>
    private static async Task<(string Code, string Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = $"http_{(int)response.StatusCode}";
        var message = response.ReasonPhrase ?? "요청 실패";

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var errBody = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl : doc.RootElement;
                code = GetString(errBody, "code", code);
                message = GetString(errBody, "message", message);
            }
        }
        catch
        {
            // 파싱 실패 → HTTP 상태 기반 기본값 유지.
        }

        return (code, message);
    }

    private static string GetString(JsonElement el, string prop, string fallback)
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;
}
