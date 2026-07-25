using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>ask_agent 의 A2A 호출 결과 — 누적 텍스트 또는 오류.</summary>
public sealed record A2aChatResult(string Text, bool IsError, string? ErrorMessage);

/// <summary>
/// 임의 엔드포인트로 /api/v1/agent/chat SSE 를 호출하는 얇은 전용 클라이언트(결정 0-2).
///
/// AgentApiClient.SendAsync 를 쓰지 못하는 이유: 그건 settings.ServerBaseUrl 에 고정돼 임의 엔드포인트를
/// 부를 수 없다. 그리고 수신측 A2aSseWriter 는 content_delta/message_stop/error 3종만 방출하므로
/// Dispatch 전체가 필요 없다 — 이 리더는 그 3종만 처리한다. AgentApiClient/Dispatch 를 건드리지 않아
/// RoundTrip 테스트가 잠근 경로에 무회귀 리스크가 없다.
/// </summary>
public sealed class A2aChatClient(HttpClient http)
{
    private const string ChatPath = "/api/v1/agent/chat";

    /// <summary>
    /// 대상 에이전트의 <paramref name="endpointBaseUrl"/> 로 프롬프트를 위임하고 최종 텍스트를 종합한다.
    /// </summary>
    public async Task<A2aChatResult> AskAsync(
        string endpointBaseUrl, string bearerToken, string prompt, int hop, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpointBaseUrl, UriKind.Absolute, out var baseUri))
            return new A2aChatResult(string.Empty, true, $"엔드포인트 URL 형식이 올바르지 않습니다: {endpointBaseUrl}");

        var target = new Uri(baseUri, ChatPath);
        var body = JsonSerializer.Serialize(new { prompt }, AgentJson.Options);

        using var req = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        req.Headers.TryAddWithoutValidation("X-A2A-Hop", hop.ToString());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new A2aChatResult(string.Empty, true, $"대상 에이전트에 연결할 수 없습니다: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var err = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                return new A2aChatResult(string.Empty, true, err);
            }

            return await ReadSseAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// AgentApiClient.SendAsync 와 동일한 SSE 프레이밍(event:/data: Trim, 빈 줄 경계)만 최소 복제.
    /// content_delta → 누적, message_stop → 종료, error → 실패. 그 외 이벤트는 무시.
    /// </summary>
    private static async Task<A2aChatResult> ReadSseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        string? eventName = null;
        var dataBuilder = new StringBuilder();

        // 프레임 하나 처리. 종료 이벤트(message_stop/error)면 결과 반환, 아니면 null(계속).
        A2aChatResult? Handle(string? evt, string data)
        {
            if (string.IsNullOrEmpty(evt) || string.IsNullOrWhiteSpace(data))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                switch (evt)
                {
                    case "content_delta":
                        if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                            text.Append(d.GetString());
                        return null;

                    case "message_stop":
                        return new A2aChatResult(text.ToString(), false, null);

                    case "error":
                        var errBody = root.TryGetProperty("error", out var e) ? e : root;
                        var message = errBody.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                            ? m.GetString()
                            : "대상 에이전트가 오류를 반환했습니다.";
                        return new A2aChatResult(text.ToString(), true, message);

                    default:
                        return null;   // message_start 등 무시.
                }
            }
            catch (JsonException)
            {
                return null;   // 깨진 data 라인 무시.
            }
        }

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
                // 스트림 종료 — message_stop 없이 끝나면 누적 텍스트만 반환(정상 종료로 취급).
                var tail = Handle(eventName, dataBuilder.ToString());
                return tail ?? new A2aChatResult(text.ToString(), false, null);
            }

            if (line.Length == 0)
            {
                var result = Handle(eventName, dataBuilder.ToString());
                eventName = null;
                dataBuilder.Clear();
                if (result is not null) return result;
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
            // 그 외(주석 ':' 등) 무시.
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                var errBody = doc.RootElement.TryGetProperty("error", out var e) ? e : doc.RootElement;
                if (errBody.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    message = m.GetString() ?? message;
            }
        }
        catch
        {
            // 파싱 실패 → HTTP 상태 기반 메시지 유지.
        }
        return message;
    }
}
