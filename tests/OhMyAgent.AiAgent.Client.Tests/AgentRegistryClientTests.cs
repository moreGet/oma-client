using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class AgentRegistryClientTests
{
    // 경로/메서드로 라우팅해 canned JSON 을 돌려주는 페이크 핸들러(포트 바인딩 없음). 요청 본문도 캡처.
    private sealed class RegistryFakeHandler(
        Func<HttpRequestMessage, string?, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Query, string? Body)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri!.Query, body));
            var (status, respBody) = respond(request, body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(respBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static AgentRegistryClient MakeClient(RegistryFakeHandler handler)
    {
        var settings = new FakeSettingsService();
        settings.Current.ServerBaseUrl = "http://localhost:8080";
        settings.Current.AuthToken = "jwt-abc";
        return new AgentRegistryClient(new HttpClient(handler), settings);
    }

    [Fact]
    public async Task Register_serializes_snake_case_and_parses_response()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.OK,
            """{"agent_id":"a-1","lease_ttl_seconds":45,"heartbeat_interval_seconds":15}"""));
        var client = MakeClient(handler);

        var resp = await client.RegisterAsync(new RegisterRequest(
            Name: "worker", EndpointUrl: "http://10.0.0.5:8080",
            Capabilities: new[] { "code-review" }, Tags: null, Model: "claude", Version: "1.3.0"));

        Assert.Equal("a-1", resp.AgentId);
        Assert.Equal(45, resp.LeaseTtlSeconds);
        Assert.Equal(15, resp.HeartbeatIntervalSeconds);

        var (method, path, _, sentBody) = handler.Requests[0];
        Assert.Equal("POST", method);
        Assert.Equal("/api/v1/agents/register", path);
        Assert.NotNull(sentBody);
        // 계약 필드명은 snake_case 여야 한다(camelCase 로 나가면 Go 서버가 못 읽음).
        Assert.Contains("\"endpoint_url\"", sentBody);
        Assert.Contains("\"capabilities\"", sentBody);
        Assert.DoesNotContain("endpointUrl", sentBody);
        // Tags=null → WhenWritingNull 로 생략.
        Assert.DoesNotContain("\"tags\"", sentBody);
    }

    [Fact]
    public async Task Heartbeat_404_throws_lease_expired()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.NotFound,
            """{"error":{"code":"not_found","message":"gone"}}"""));
        var client = MakeClient(handler);

        await Assert.ThrowsAsync<AgentLeaseExpiredException>(() => client.HeartbeatAsync("a-1"));
    }

    [Fact]
    public async Task Heartbeat_200_parses_response()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.OK,
            """{"status":"online","lease_ttl_seconds":45}"""));
        var client = MakeClient(handler);

        var resp = await client.HeartbeatAsync("a-1");
        Assert.Equal("online", resp.Status);
        Assert.Equal(45, resp.LeaseTtlSeconds);
        Assert.Equal("/api/v1/agents/a-1/heartbeat", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Discover_reads_envelope()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.OK,
            """{"agents":[{"agent_id":"a-1","name":"w","endpoint_url":"http://x","capabilities":["c"],"status":"online","last_heartbeat_at":"2026-07-20T00:00:00Z"}]}"""));
        var client = MakeClient(handler);

        var list = await client.DiscoverAsync(new DiscoverQuery(Capability: "c", Status: "online", ExcludeSelf: "self-1"));
        Assert.Single(list);
        Assert.Equal("a-1", list[0].AgentId);
        Assert.Equal("online", list[0].Status);

        // 쿼리스트링에 필터가 반영돼야 한다.
        var query = handler.Requests[0].Query!;
        Assert.Contains("capability=c", query);
        Assert.Contains("status=online", query);
        Assert.Contains("exclude_self=self-1", query);
    }

    [Fact]
    public async Task Discover_reads_flat_array()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.OK,
            """[{"agent_id":"a-2","name":"w2","endpoint_url":"http://y","capabilities":[],"status":"stale","last_heartbeat_at":null}]"""));
        var client = MakeClient(handler);

        var list = await client.DiscoverAsync(new DiscoverQuery());
        Assert.Single(list);
        Assert.Equal("a-2", list[0].AgentId);
    }

    [Fact]
    public async Task Discover_offline_returns_empty()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.InternalServerError, "{}"));
        var client = MakeClient(handler);

        var list = await client.DiscoverAsync(new DiscoverQuery(Capability: "c"));
        Assert.Empty(list);
    }

    [Fact]
    public async Task Get_404_returns_null()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.NotFound, "{}"));
        var client = MakeClient(handler);

        Assert.Null(await client.GetAsync("missing"));
    }

    [Fact]
    public async Task MintToken_404_throws_agent_exception()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.NotFound, "{}"));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.MintA2aTokenAsync("gone"));
        Assert.Contains("토큰 발급 실패", ex.Message);
    }

    [Fact]
    public async Task MintToken_200_parses_response()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.OK,
            """{"token":"jwt-xyz","expires_in_seconds":120,"audience_agent_id":"a-9"}"""));
        var client = MakeClient(handler);

        var token = await client.MintA2aTokenAsync("a-9");
        Assert.Equal("jwt-xyz", token.Token);
        Assert.Equal(120, token.ExpiresInSeconds);
        Assert.Equal("a-9", token.AudienceAgentId);
        Assert.Equal("/api/v1/agents/a-9/token", handler.Requests[0].Path);
    }

    [Fact]
    public async Task PublicKey_parses_response()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.OK,
            """{"kid":"k1","alg":"ES256","public_key_pem":"-----BEGIN PUBLIC KEY-----\nAAA\n-----END PUBLIC KEY-----"}"""));
        var client = MakeClient(handler);

        var key = await client.GetA2aPublicKeyAsync();
        Assert.Equal("k1", key.Kid);
        Assert.Equal("ES256", key.Alg);
        Assert.Contains("BEGIN PUBLIC KEY", key.PublicKeyPem);
        Assert.Equal("/api/v1/agents/a2a-public-key", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Deregister_swallows_errors()
    {
        var handler = new RegistryFakeHandler((_, _) => (HttpStatusCode.InternalServerError, "{}"));
        var client = MakeClient(handler);

        // 예외 없이 완료돼야 한다(멱등 best-effort).
        await client.DeregisterAsync("a-1");
        Assert.Equal("DELETE", handler.Requests[0].Method);
    }
}
