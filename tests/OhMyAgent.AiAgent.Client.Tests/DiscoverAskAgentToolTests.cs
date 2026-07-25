using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class DiscoverAskAgentToolTests
{
    // ── 페이크 레지스트리 ──
    private sealed class FakeRegistry : IAgentRegistryClient
    {
        public DiscoverQuery? LastQuery;
        public List<AgentDescriptor> DiscoverResult = new();
        public AgentDescriptor? GetResult;
        public string? LastGetId;
        public string? LastMintTarget;
        public Func<string, A2aToken> MintFunc = _ => new A2aToken("tok-default", 120, "aud");

        public Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<HeartbeatResponse> HeartbeatAsync(string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeregisterAsync(string agentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentDescriptor>> DiscoverAsync(DiscoverQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult((IReadOnlyList<AgentDescriptor>)DiscoverResult);
        }

        public Task<AgentDescriptor?> GetAsync(string agentId, CancellationToken ct = default)
        {
            LastGetId = agentId;
            return Task.FromResult(GetResult);
        }

        public Task<A2aToken> MintA2aTokenAsync(string targetAgentId, CancellationToken ct = default)
        {
            LastMintTarget = targetAgentId;
            return Task.FromResult(MintFunc(targetAgentId));
        }

        public Task<A2aPublicKey> GetA2aPublicKeyAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // A2A 대상 응답을 canned SSE 로 돌려주고 X-A2A-Hop 을 캡처하는 핸들러.
    private sealed class HopCapturingHandler(string sse) : HttpMessageHandler
    {
        public string? CapturedHop;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.TryGetValues("X-A2A-Hop", out var hops))
                foreach (var h in hops) CapturedHop = h;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private static AgentDescriptor Desc(string id, string name, string endpoint = "http://target:9000") =>
        new(id, name, endpoint, new[] { "code-review" }, null, "claude", "online", DateTimeOffset.UtcNow);

    private static ToolContext Ctx(int hop) =>
        new(new WorkspaceContext(new FakeSettingsService()), PermissionMode.FullAuto, hop);

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string StopFrame => "event: content_delta\ndata: {\"delta\":\"done\"}\n\nevent: message_stop\ndata: {\"stop_reason\":\"end_turn\"}\n\n";

    // ── discover_agents ──

    [Fact]
    public async Task Discover_passes_filters_and_exclude_self()
    {
        var registry = new FakeRegistry { DiscoverResult = { Desc("a-1", "reviewer") } };
        var keyStore = new BrokerKeyStore();
        keyStore.SetAgentId("self-1");
        var tool = new DiscoverAgentsTool(registry, keyStore);

        var result = await tool.ExecuteAsync(Args("""{"capability":"code-review","status":"online"}"""), Ctx(0));

        Assert.False(result.IsError);
        Assert.Equal("code-review", registry.LastQuery!.Capability);
        Assert.Equal("online", registry.LastQuery.Status);
        Assert.Equal("self-1", registry.LastQuery.ExcludeSelf);
        Assert.Contains("\"agent_id\"", result.Content);
        Assert.Contains("a-1", result.Content);
        Assert.Contains("reviewer", result.Content);
    }

    [Fact]
    public void Discover_schema_has_no_raw_url_field()
    {
        var tool = new DiscoverAgentsTool(new FakeRegistry(), new BrokerKeyStore());
        var schema = tool.ParametersSchema.GetRawText();
        Assert.DoesNotContain("url", schema);
        Assert.DoesNotContain("endpoint", schema);
    }

    // ── ask_agent ──

    [Fact]
    public async Task Ask_increments_hop_from_inbound()
    {
        var registry = new FakeRegistry { GetResult = Desc("a-1", "reviewer") };
        var handler = new HopCapturingHandler(StopFrame);
        var chat = new A2aChatClient(new HttpClient(handler));
        var tool = new AskAgentTool(registry, chat, new BrokerKeyStore());

        // 수신 홉 2 → 전파 홉 3.
        var result = await tool.ExecuteAsync(Args("""{"agent_id":"a-1","prompt":"review this"}"""), Ctx(2));

        Assert.False(result.IsError);
        Assert.Equal("3", handler.CapturedHop);
    }

    [Fact]
    public async Task Ask_resolves_by_agent_id()
    {
        var registry = new FakeRegistry { GetResult = Desc("a-1", "reviewer") };
        var chat = new A2aChatClient(new HttpClient(new HopCapturingHandler(StopFrame)));
        var tool = new AskAgentTool(registry, chat, new BrokerKeyStore());

        var result = await tool.ExecuteAsync(Args("""{"agent_id":"a-1","prompt":"hi"}"""), Ctx(0));

        Assert.Equal("a-1", registry.LastGetId);
        Assert.Equal("a-1", registry.LastMintTarget);
        Assert.Contains("reviewer", result.Content);
    }

    [Fact]
    public async Task Ask_by_capability_picks_first_candidate()
    {
        var registry = new FakeRegistry
        {
            DiscoverResult = { Desc("a-1", "first"), Desc("a-2", "second") },
        };
        var chat = new A2aChatClient(new HttpClient(new HopCapturingHandler(StopFrame)));
        var tool = new AskAgentTool(registry, chat, new BrokerKeyStore());

        var result = await tool.ExecuteAsync(Args("""{"capability":"code-review","prompt":"hi"}"""), Ctx(0));

        Assert.False(result.IsError);
        Assert.Equal("a-1", registry.LastMintTarget);   // 첫 후보 결정적 선택
        Assert.Contains("first", result.Content);
    }

    [Fact]
    public async Task Ask_by_capability_no_candidate_fails_clearly()
    {
        var registry = new FakeRegistry();   // DiscoverResult 빈 목록
        var chat = new A2aChatClient(new HttpClient(new HopCapturingHandler(StopFrame)));
        var tool = new AskAgentTool(registry, chat, new BrokerKeyStore());

        var result = await tool.ExecuteAsync(Args("""{"capability":"nonexistent","prompt":"hi"}"""), Ctx(0));

        Assert.True(result.IsError);
        Assert.Contains("온라인 에이전트가 없습니다", result.Content);
    }

    [Fact]
    public async Task Ask_token_mint_failure_fails()
    {
        var registry = new FakeRegistry
        {
            GetResult = Desc("a-1", "reviewer"),
            MintFunc = _ => throw new AgentException("A2A 토큰 발급 실패: 대상 에이전트가 존재하지 않습니다."),
        };
        var chat = new A2aChatClient(new HttpClient(new HopCapturingHandler(StopFrame)));
        var tool = new AskAgentTool(registry, chat, new BrokerKeyStore());

        var result = await tool.ExecuteAsync(Args("""{"agent_id":"a-1","prompt":"hi"}"""), Ctx(0));

        Assert.True(result.IsError);
        Assert.Contains("토큰 발급 실패", result.Content);
    }

    [Fact]
    public async Task Ask_missing_agent_returns_fail()
    {
        var registry = new FakeRegistry { GetResult = null };   // GetAsync → null
        var chat = new A2aChatClient(new HttpClient(new HopCapturingHandler(StopFrame)));
        var tool = new AskAgentTool(registry, chat, new BrokerKeyStore());

        var result = await tool.ExecuteAsync(Args("""{"agent_id":"gone","prompt":"hi"}"""), Ctx(0));

        Assert.True(result.IsError);
        Assert.Contains("찾을 수 없습니다", result.Content);
    }

    [Fact]
    public async Task Ask_empty_prompt_fails()
    {
        var tool = new AskAgentTool(new FakeRegistry(), new A2aChatClient(new HttpClient(new HopCapturingHandler(StopFrame))), new BrokerKeyStore());
        var result = await tool.ExecuteAsync(Args("""{"agent_id":"a-1","prompt":""}"""), Ctx(0));
        Assert.True(result.IsError);
        Assert.Contains("prompt", result.Content);
    }

    [Fact]
    public async Task Ask_no_target_fails()
    {
        var tool = new AskAgentTool(new FakeRegistry(), new A2aChatClient(new HttpClient(new HopCapturingHandler(StopFrame))), new BrokerKeyStore());
        var result = await tool.ExecuteAsync(Args("""{"prompt":"hi"}"""), Ctx(0));
        Assert.True(result.IsError);
        Assert.Contains("agent_id 또는 capability", result.Content);
    }

    [Fact]
    public void Ask_schema_has_no_raw_url_field()
    {
        var tool = new AskAgentTool(new FakeRegistry(), new A2aChatClient(new HttpClient(new HopCapturingHandler(StopFrame))), new BrokerKeyStore());
        var schema = tool.ParametersSchema.GetRawText();
        Assert.DoesNotContain("url", schema);
        Assert.DoesNotContain("endpoint", schema);
    }
}
