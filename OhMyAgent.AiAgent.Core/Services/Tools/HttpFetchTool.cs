using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class HttpFetchTool : ITool
{
    private const int MaxBodyBytes = 1024 * 1024; // 1MB 트렁케이트

    /// <summary>
    /// 리다이렉트 최대 추적 횟수. 자동 추적을 끄고 직접 따라가는 이유는, 매 홉마다 <see cref="UrlGuard"/> 로
    /// 재검증하기 위함이다 — 공개 URL 이 302 로 169.254.169.254 나 로컬 주소로 유도할 수 있다.
    /// </summary>
    private const int MaxRedirects = 5;

    // 앱의 API용 HttpClient 와 분리된 전용 인스턴스.
    private readonly HttpClient _http;

    public HttpFetchTool(HttpClient? http = null)
    {
        _http = http ?? CreateDefaultClient();
    }

    /// <summary>자동 리다이렉트를 끈 기본 클라이언트(홉마다 직접 검증하기 위함).</summary>
    internal static HttpClient CreateDefaultClient()
        => new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"url":{"type":"string"},"method":{"type":"string"},"headers":{"type":"object"},"body":{"type":"string"}},"required":["url"]}
        """);

    public string Name => "http_fetch";
    public string Description => "Make an HTTP request to an internal/intranet URL. Args: url, method (default GET), headers (object), body. Returns status, headers, and body text (truncated to 1MB).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Execute;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var url = ToolSchemas.GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Fail("url 가 비어 있습니다.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ToolResult.Fail($"url 형식이 올바르지 않습니다: {url}");

        var methodName = ToolSchemas.GetString(args, "method");
        var method = string.IsNullOrWhiteSpace(methodName)
            ? HttpMethod.Get
            : new HttpMethod(methodName.ToUpperInvariant());

        var body = ToolSchemas.GetString(args, "body");

        try
        {
            var (response, finalUri, denied) = await SendFollowingRedirectsAsync(method, uri, body, args, ct)
                .ConfigureAwait(false);

            if (denied is not null || response is null)
                return ToolResult.Fail(denied ?? "HTTP 응답을 받지 못했습니다.");

            using (response)
            {
                var headers = new Dictionary<string, string>();
                foreach (var h in response.Headers)
                    headers[h.Key] = string.Join(", ", h.Value);
                foreach (var h in response.Content.Headers)
                    headers[h.Key] = string.Join(", ", h.Value);

                var (text, truncated, read) = await ReadCappedBodyAsync(response, ct).ConfigureAwait(false);

                var payload = new
                {
                    status = (int)response.StatusCode,
                    reason = response.ReasonPhrase,
                    url = finalUri.ToString(),
                    headers,
                    body = text,
                    truncated,
                    content_length = read
                };

                var isError = !response.IsSuccessStatusCode;
                return new ToolResult(JsonSerializer.Serialize(payload, AgentJson.Options), isError);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"HTTP 요청 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 리다이렉트를 직접 따라가며 <b>매 홉마다</b> <see cref="UrlGuard"/> 로 검증한다.
    /// 자동 추적(AllowAutoRedirect)에 맡기면 최초 URL 만 검증되고 302 로 내부 주소에 도달할 수 있다.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, Uri Final, string? Denied)> SendFollowingRedirectsAsync(
        HttpMethod method, Uri uri, string? body, JsonElement args, CancellationToken ct)
    {
        var current = uri;

        for (var hop = 0; ; hop++)
        {
            var check = await UrlGuard.CheckAsync(current, ct).ConfigureAwait(false);
            if (!check.Allowed)
                return (null, current, check.Reason);

            var request = new HttpRequestMessage(method, current);
            if (!string.IsNullOrEmpty(body))
                request.Content = new StringContent(body, Encoding.UTF8);
            ApplyHeaders(request, args);

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            request.Dispose();

            var location = response.Headers.Location;
            var isRedirect = (int)response.StatusCode is >= 300 and < 400 && location is not null;
            if (!isRedirect)
                return (response, current, null);

            if (hop >= MaxRedirects)
            {
                response.Dispose();
                return (null, current, $"리다이렉트가 너무 많습니다(최대 {MaxRedirects}회).");
            }

            // 상대 Location 을 절대 URI 로. 다음 루프에서 다시 검증된다.
            current = location!.IsAbsoluteUri ? location : new Uri(current, location);
            response.Dispose();
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, JsonElement args)
    {
        // headers (object): 콘텐츠 헤더는 Content 에, 나머지는 요청 헤더에.
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("headers", out var headersEl)
            || headersEl.ValueKind != JsonValueKind.Object)
            return;

        foreach (var header in headersEl.EnumerateObject())
        {
            if (header.Value.ValueKind != JsonValueKind.String)
                continue;
            var value = header.Value.GetString() ?? string.Empty;
            if (!request.Headers.TryAddWithoutValidation(header.Name, value))
                request.Content?.Headers.TryAddWithoutValidation(header.Name, value);
        }
    }

    /// <summary>
    /// 상한까지만 스트림에서 읽는다. 종전에는 ReadAsByteArrayAsync 로 본문 <b>전체</b>를 메모리에 올린 뒤
    /// 1MB 로 잘라, 수 GB 를 흘려보내는 URL 하나로 앱을 OOM 시킬 수 있었다(ResponseHeadersRead 를
    /// 요청해 놓고도 이득이 없었다).
    /// </summary>
    private static async Task<(string Text, bool Truncated, long Read)> ReadCappedBodyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[MaxBodyBytes + 1];   // 상한 + 1 → 초과 여부를 판별하되 그 이상은 읽지 않는다.
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        var truncated = total > MaxBodyBytes;
        var length = truncated ? MaxBodyBytes : total;
        var text = Encoding.UTF8.GetString(buffer, 0, length);
        if (truncated)
            text += $"\n\n[... 응답이 {MaxBodyBytes} 바이트로 잘렸습니다 ...]";

        return (text, truncated, length);
    }
}
