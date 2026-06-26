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

    // 앱의 API용 HttpClient 와 분리된 전용 인스턴스.
    private readonly HttpClient _http;

    public HttpFetchTool(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

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

        using var request = new HttpRequestMessage(method, uri);

        var body = ToolSchemas.GetString(args, "body");
        if (!string.IsNullOrEmpty(body))
            request.Content = new StringContent(body, Encoding.UTF8);

        // headers (object): 콘텐츠 헤더는 Content 에, 나머지는 요청 헤더에.
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("headers", out var headersEl)
            && headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headersEl.EnumerateObject())
            {
                if (header.Value.ValueKind != JsonValueKind.String)
                    continue;
                var value = header.Value.GetString() ?? string.Empty;
                if (!request.Headers.TryAddWithoutValidation(header.Name, value))
                    request.Content?.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            var headers = new Dictionary<string, string>();
            foreach (var h in response.Headers)
                headers[h.Key] = string.Join(", ", h.Value);
            foreach (var h in response.Content.Headers)
                headers[h.Key] = string.Join(", ", h.Value);

            var raw = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var truncated = raw.Length > MaxBodyBytes;
            var slice = truncated ? raw[..MaxBodyBytes] : raw;
            var text = Encoding.UTF8.GetString(slice);
            if (truncated)
                text += $"\n\n[... 응답이 {MaxBodyBytes} 바이트로 잘렸습니다 ...]";

            var payload = new
            {
                status = (int)response.StatusCode,
                reason = response.ReasonPhrase,
                headers,
                body = text,
                truncated,
                content_length = raw.Length
            };

            var isError = !response.IsSuccessStatusCode;
            return new ToolResult(JsonSerializer.Serialize(payload, AgentJson.Options), isError);
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
}
