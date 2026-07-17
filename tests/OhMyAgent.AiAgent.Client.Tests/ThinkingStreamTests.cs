using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 확장 사고 스트림. 계약:
///  (1) thinking_delta 는 별도 AgentThinkingDelta 로 나오고, 프로즈/도구 전에 AgentThinkingComplete 로 닫힌다.
///  (2) 사고 원문·서명을 assistant 이력에 저장한다 — 다음 요청 재생에 필요(없으면 서버가 400).
///  (3) 서명 없는 사고는 저장하지 않는다(재생 불가 → 400).
///  (4) 설정이 꺼져 있으면 요청에 thinking 을 싣지 않는다(기본 모델 보호).
/// </summary>
public class ThinkingStreamTests
{
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;

    private static AgentOrchestrator Build(IAgentApiClient api, FakeSettingsService settings)
    {
        var ws = new WorkspaceContext(settings);
        return new AgentOrchestrator(
            api, new ToolRegistry([]), new PermissionService(settings), ws, settings,
            new AllowAllPolicy(), new ContextCompactor(api, settings));
    }

    private static async Task<List<AgentEvent>> RunAsync(AgentOrchestrator orch, AgentSession session)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in orch.RunAsync("목표", session))
            events.Add(e);
        return events;
    }

    // ── 스트림 → 이벤트 ──

    [Fact]
    public async Task ThinkingDeltas_BecomeThinkingEvents()
    {
        var api = new ScriptedApi(
            [new ThinkingDelta("음, "), new ThinkingDelta("파일을 먼저 보자"),
             new ContentDelta("확인했습니다"),
             new MessageStop("end_turn", new Usage(0, 0, 0), "음, 파일을 먼저 보자", "sig-1")]);

        var events = await RunAsync(Build(api, Settings()), new AgentSession());

        var thinking = string.Concat(events.OfType<AgentThinkingDelta>().Select(e => e.Text));
        Assert.Equal("음, 파일을 먼저 보자", thinking);
        Assert.Contains(events, e => e is AgentThinkingComplete);
    }

    [Fact]
    public async Task ThinkingClosesBeforeProse()
    {
        var api = new ScriptedApi(
            [new ThinkingDelta("생각"), new ContentDelta("답변"),
             new MessageStop("end_turn", new Usage(0, 0, 0), "생각", "sig-1")]);

        var events = await RunAsync(Build(api, Settings()), new AgentSession());

        var completeIdx = events.FindIndex(e => e is AgentThinkingComplete);
        var firstProse = events.FindIndex(e => e is AgentTextDelta);

        Assert.True(completeIdx >= 0, "사고가 닫히지 않았다");
        Assert.True(completeIdx < firstProse, "사고 블록이 프로즈보다 늦게 닫혔다");
    }

    // ── 재생 저장(핵심) ──

    [Fact]
    public async Task ThinkingAndSignature_StoredOnAssistantMessage()
    {
        var api = new ScriptedApi(
            [new ThinkingDelta("추론 과정"),
             new MessageStop("end_turn", new Usage(0, 0, 0), "추론 과정", "sig-xyz")]);

        var session = new AgentSession();
        await RunAsync(Build(api, Settings()), session);

        var assistant = session.Messages.Last(m => m.Role == MessageRole.Assistant);
        Assert.Equal("추론 과정", assistant.Thinking);
        Assert.Equal("sig-xyz", assistant.ThinkingSignature);
    }

    [Fact]
    public async Task ThinkingWithoutSignature_IsNotStored()
    {
        // 서명 없는 사고는 재생 불가 — 저장하면 다음 요청에서 400 을 부른다.
        var api = new ScriptedApi(
            [new ThinkingDelta("서명 없는 사고"),
             new MessageStop("end_turn", new Usage(0, 0, 0), "서명 없는 사고", "")]);

        var session = new AgentSession();
        await RunAsync(Build(api, Settings()), session);

        var assistant = session.Messages.Last(m => m.Role == MessageRole.Assistant);
        Assert.Null(assistant.Thinking);
        Assert.Null(assistant.ThinkingSignature);
    }

    [Fact]
    public async Task StoredThinking_SerializesForReplay()
    {
        var api = new ScriptedApi(
            [new ThinkingDelta("x"), new MessageStop("end_turn", new Usage(0, 0, 0), "재생될 사고", "sig-9")]);

        var session = new AgentSession();
        await RunAsync(Build(api, Settings()), session);

        var assistant = session.Messages.Last(m => m.Role == MessageRole.Assistant);
        var json = JsonSerializer.Serialize(assistant, AgentJson.Options);

        // 한글은 STJ 기본 인코더가 \uXXXX 로 이스케이프하므로 원문 리터럴 대신 왕복으로 검증한다.
        Assert.Contains("thinking_signature", json);   // 와이어 키(ASCII)는 그대로
        var roundTripped = JsonSerializer.Deserialize<AgentMessage>(json, AgentJson.Options)!;
        Assert.Equal("재생될 사고", roundTripped.Thinking);
        Assert.Equal("sig-9", roundTripped.ThinkingSignature);
    }

    // ── 설정 게이트 ──

    [Fact]
    public async Task ThinkingOff_RequestOmitsThinking()
    {
        var settings = Settings(showThinking: false);
        var api = new CapturingApi([new ContentDelta("답"), new MessageStop("end_turn", new Usage(0, 0, 0))]);

        await RunAsync(Build(api, settings), new AgentSession());

        Assert.NotNull(api.LastRequest);
        Assert.Null(api.LastRequest!.Thinking);   // 기본 모델(3.5-sonnet) 보호 — 꺼져 있으면 절대 안 보낸다
    }

    [Fact]
    public async Task ThinkingOn_RequestSendsAdaptive()
    {
        var settings = Settings(showThinking: true);
        var api = new CapturingApi([new ContentDelta("답"), new MessageStop("end_turn", new Usage(0, 0, 0))]);

        await RunAsync(Build(api, settings), new AgentSession());

        Assert.NotNull(api.LastRequest!.Thinking);
        Assert.Equal("adaptive", api.LastRequest.Thinking!.Type);
    }

    [Fact]
    public async Task ThinkingOff_NoThinkingEventsEvenIfServerSends()
    {
        // 방어: 설정이 꺼져 있어도 서버가 사고를 보내면(다른 클라이언트가 켰다든지) 표시 자체는 동작해야 한다.
        var api = new ScriptedApi(
            [new ThinkingDelta("서버발 사고"), new MessageStop("end_turn", new Usage(0, 0, 0), "서버발 사고", "s")]);

        var events = await RunAsync(Build(api, Settings(showThinking: false)), new AgentSession());

        // 요청엔 안 실었지만, 받은 사고는 그대로 흘려보낸다(표시는 UI 설정과 무관하게 이벤트 기반).
        Assert.Contains(events, e => e is AgentThinkingDelta);
    }

    private static FakeSettingsService Settings(bool showThinking = true)
    {
        var s = new FakeSettingsService();
        s.Current.PermissionMode = PermissionMode.FullAuto;
        s.Current.MaxIterations = 3;
        s.Current.ShowThinking = showThinking;
        return s;
    }
}

/// <summary>보낸 요청을 붙잡아두는 API 스텁(요청 검증용).</summary>
internal sealed class CapturingApi(params AgentStreamEvent[] events) : StubAgentApi
{
    public AgentRequest? LastRequest { get; private set; }

    public override async IAsyncEnumerable<AgentStreamEvent> SendAsync(
        AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastRequest = request;
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
        await Task.CompletedTask;
    }
}
