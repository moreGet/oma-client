using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class AgentApiClient(HttpClient httpClient, ISettingsService settings) : IAgentApiClient
{
    private const string ChatPath   = "/api/v1/agent/chat";
    private const string HealthPath = "/api/v1/health";
    private const string ModelsPath = "/api/v1/models";

    public async IAsyncEnumerable<AgentStreamEvent> SendAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(request, AgentJson.Options);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, ChatPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
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

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, HealthPath);
            ApplyAuth(req);
            using var resp = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ModelsPath);
            ApplyAuth(req);
            using var resp = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
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

    private void ApplyAuth(HttpRequestMessage req)
    {
        var token = settings.Current.AuthToken;
        if (string.IsNullOrWhiteSpace(token))
            return;

        var scheme = settings.Current.AuthScheme;
        if (string.Equals(scheme, "ApiKey", StringComparison.OrdinalIgnoreCase))
            req.Headers.TryAddWithoutValidation("X-Api-Key", token);
        else
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
                    return new ContentDelta(GetString(root, "text"));

                case "tool_call":
                    var args = root.TryGetProperty("arguments", out var argsEl)
                        ? argsEl.Clone()
                        : EmptyObject();
                    return new ToolCallEvent(
                        GetString(root, "id"),
                        GetString(root, "name"),
                        args);

                case "message_stop":
                    var stopReason = GetString(root, "stop_reason");
                    var usage = root.TryGetProperty("usage", out var usageEl)
                        ? usageEl.Deserialize<Usage>(AgentJson.Options) ?? new Usage(0, 0)
                        : new Usage(0, 0);
                    return new MessageStop(stopReason, usage);

                case "error":
                    return new ErrorEvent(
                        GetString(root, "code"),
                        GetString(root, "message"));

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
                if (doc.RootElement.TryGetProperty("error", out var errEl))
                {
                    code = GetString(errEl, "code", code);
                    message = GetString(errEl, "message", message);
                }
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
}
