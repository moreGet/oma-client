using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class A2aChatClientTests
{
    // SSE(또는 오류) 응답을 canned 로 돌려주고 요청 헤더/본문을 캡처하는 핸들러.
    private sealed class ChatFakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? CapturedHop;
        public AuthenticationHeaderValue? CapturedAuth;
        public string? CapturedBody;
        public string? CapturedPath;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CapturedPath = request.RequestUri!.AbsolutePath;
            CapturedAuth = request.Headers.Authorization;
            if (request.Headers.TryGetValues("X-A2A-Hop", out var hops))
                foreach (var h in hops) CapturedHop = h;
            CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        }
    }

    private static string Sse(params string[] frames) => string.Concat(frames);
    private static string Frame(string ev, string dataJson) => $"event: {ev}\ndata: {dataJson}\n\n";

    [Fact]
    public async Task Accumulates_content_deltas_until_message_stop()
    {
        var sse = Sse(
            Frame("message_start", """{"id":"r1","model":"m"}"""),
            Frame("content_delta", """{"delta":"hello "}"""),
            Frame("content_delta", """{"delta":"world"}"""),
            Frame("message_stop", """{"stop_reason":"end_turn"}"""));
        var handler = new ChatFakeHandler(HttpStatusCode.OK, sse);
        var client = new A2aChatClient(new HttpClient(handler));

        var result = await client.AskAsync("http://target:9000", "tok", "hi", hop: 3, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public async Task Error_event_yields_is_error()
    {
        var sse = Sse(
            Frame("content_delta", """{"delta":"partial"}"""),
            Frame("error", """{"error":{"code":"boom","message":"downstream failed"}}"""));
        var handler = new ChatFakeHandler(HttpStatusCode.OK, sse);
        var client = new A2aChatClient(new HttpClient(handler));

        var result = await client.AskAsync("http://target:9000", "tok", "hi", hop: 1, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("downstream failed", result.ErrorMessage);
    }

    [Fact]
    public async Task Non_2xx_yields_is_error()
    {
        var handler = new ChatFakeHandler(HttpStatusCode.Unauthorized,
            """{"error":{"code":"unauthorized","message":"bad token"}}""");
        var client = new A2aChatClient(new HttpClient(handler));

        var result = await client.AskAsync("http://target:9000", "tok", "hi", hop: 1, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("bad token", result.ErrorMessage);
    }

    [Fact]
    public async Task Attaches_hop_bearer_and_posts_to_chat_path()
    {
        var sse = Frame("message_stop", """{"stop_reason":"end_turn"}""");
        var handler = new ChatFakeHandler(HttpStatusCode.OK, sse);
        var client = new A2aChatClient(new HttpClient(handler));

        await client.AskAsync("http://target:9000", "tok-abc", "delegate this", hop: 4, CancellationToken.None);

        Assert.Equal("4", handler.CapturedHop);
        Assert.NotNull(handler.CapturedAuth);
        Assert.Equal("Bearer", handler.CapturedAuth!.Scheme);
        Assert.Equal("tok-abc", handler.CapturedAuth.Parameter);
        Assert.Equal("/api/v1/agent/chat", handler.CapturedPath);
        Assert.Contains("delegate this", handler.CapturedBody);
    }

    [Fact]
    public async Task Invalid_endpoint_yields_is_error()
    {
        var client = new A2aChatClient(new HttpClient(new ChatFakeHandler(HttpStatusCode.OK, "")));
        var result = await client.AskAsync("not-a-url", "tok", "hi", hop: 1, CancellationToken.None);
        Assert.True(result.IsError);
    }
}
