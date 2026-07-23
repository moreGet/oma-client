using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 대칭 잠금(스펙 §④): A2aSseWriter 가 만든 SSE 바이트를 실제 AgentApiClient 의 SSE 리더/Dispatch 로
/// 역투입해 원래 orchestrator 이벤트가 복원되는지 확인한다. Dispatch ↔ SseWriter 대칭 회귀 방지.
/// </summary>
public class A2aRoundTripTests
{
    // 미리 만든 SSE 바이트를 단일 응답으로 돌려주는 페이크 핸들러(포트 바인딩 없이 리더 경로 실측).
    private sealed class CannedSseHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(resp);
        }
    }

    private static async Task<byte[]> BuildWriterPayloadAsync()
    {
        using var ms = new MemoryStream();
        var writer = new A2aSseWriter(ms, "run1", "model-x", new A2aOptions());
        await writer.WriteMessageStartAsync(CancellationToken.None);
        await writer.WriteAsync(new AgentTextDelta("hi"), CancellationToken.None);
        await writer.WriteAsync(new AgentDone("hi", new Usage(3, 5, 8)), CancellationToken.None);
        return ms.ToArray();
    }

    private static AgentRequest MinimalRequest() => new(
        Model: "model-x",
        Stream: true,
        MaxTokens: 1024,
        Messages: new[] { AgentMessage.User("hi") },
        Tools: Array.Empty<ToolSchema>(),
        Metadata: new RequestMetadata("Linux", "/ws", "test"));

    [Fact]
    public async Task Writer_output_round_trips_through_AgentApiClient_parser()
    {
        var payload = await BuildWriterPayloadAsync();

        var settings = new FakeSettingsService();
        settings.Current.ServerBaseUrl = "http://localhost:8080";
        using var http = new HttpClient(new CannedSseHandler(payload));
        var api = new AgentApiClient(http, settings);

        var events = new List<AgentStreamEvent>();
        await foreach (var ev in api.SendAsync(MinimalRequest(), CancellationToken.None))
            events.Add(ev);

        // message_start → content_delta("hi") → message_stop(end_turn, usage 3/5/8) 로 복원.
        Assert.Collection(events,
            e => Assert.IsType<MessageStart>(e),
            e =>
            {
                var d = Assert.IsType<ContentDelta>(e);
                Assert.Equal("hi", d.Text);
            },
            e =>
            {
                var stop = Assert.IsType<MessageStop>(e);
                Assert.Equal("end_turn", stop.StopReason);
                Assert.Equal(3, stop.Usage.PromptTokens);
                Assert.Equal(5, stop.Usage.CompletionTokens);
                Assert.Equal(8, stop.Usage.TotalTokens);
            });
    }
}
