using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class ChatService(HttpClient httpClient) : IChatService
{
    public async IAsyncEnumerable<string> StreamResponseAsync(
        UserMessagesDto request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = JsonConvert.SerializeObject(new
        {
            session_id = request.SessionId,
            content    = request.Content,
            domain     = request.Domain
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/stream")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AgentException("서버 연결에 실패했습니다. 잠시 후 다시 시도해주세요.", ex);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;

            // SSE 형식: "data: <payload>"
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line[6..].Trim();
            if (data == "[DONE]") yield break;
            if (!string.IsNullOrEmpty(data))
                yield return data;
        }
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await httpClient.GetAsync("/api/v1/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
