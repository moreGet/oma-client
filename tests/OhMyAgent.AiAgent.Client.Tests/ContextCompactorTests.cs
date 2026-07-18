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
/// 컨텍스트 컴팩션. 핵심 계약:
///  (1) session.Messages 는 절대 변하지 않는다 — 디스크·동기화에 남는 이력은 온전해야 한다.
///  (2) tool_call_id 짝을 깨지 않는다 — 깨지면 프로바이더가 요청을 통째로 거부한다.
///  (3) 요약을 캐시한다 — 매 턴 다시 요약하면 절약분보다 비용이 크다.
///  (4) 실패해도 대화를 막지 않는다.
/// </summary>
public class ContextCompactorTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static AgentSession SessionWith(params AgentMessage[] messages)
    {
        var s = new AgentSession();
        s.Messages.AddRange(messages);
        return s;
    }

    private static string Big(int chars) => new('x', chars);

    /// <summary>user → assistant(tool_call) → tool 결과 한 턴.</summary>
    private static IEnumerable<AgentMessage> Turn(string userText, string callId, int toolResultChars)
    {
        yield return AgentMessage.User(userText);
        yield return AgentMessage.Assistant(null, [new ToolCall(callId, "read_file", EmptyArgs)]);
        yield return AgentMessage.ToolResultMsg(callId, Big(toolResultChars), isError: false);
    }

    private static AgentSession LongSession(int turns, int charsPerTurn)
    {
        var s = new AgentSession();
        s.Messages.Add(AgentMessage.System("시스템 프롬프트"));
        for (var i = 0; i < turns; i++)
            s.Messages.AddRange(Turn($"요청 {i}", $"call-{i}", charsPerTurn));
        return s;
    }

    // ── BuildWireMessages: 순수 투영 ──

    [Fact]
    public void BuildWire_WithoutCompaction_ReturnsEverything()
    {
        var s = SessionWith(AgentMessage.System("sys"), AgentMessage.User("안녕"));

        var wire = ContextCompactor.BuildWireMessages(s);

        Assert.Equal(2, wire.Count);
        Assert.Equal(MessageRole.System, wire[0].Role);
    }

    [Fact]
    public void BuildWire_AlwaysKeepsSystemPromptFirst()
    {
        var s = LongSession(5, 100);
        s.CompactionSummary = "요약";
        s.CompactedThrough = 10;

        var wire = ContextCompactor.BuildWireMessages(s);

        Assert.Equal("시스템 프롬프트", wire[0].Content);
    }

    [Fact]
    public void BuildWire_SummaryIsSystemRole_SoItCannotBreakToolPairing()
    {
        // user 로 넣으면 절단 직후의 user 와 연속 user 가 되어 프로바이더별로 병합/거부 위험이 있고,
        // 메시지 시퀀스에 끼어들어 짝을 흔든다. system 은 Claude 어댑터가 System 파라미터로 빼간다.
        var s = LongSession(5, 100);
        s.CompactionSummary = "앞부분 요약";
        s.CompactedThrough = 7;

        var wire = ContextCompactor.BuildWireMessages(s);

        Assert.Equal(MessageRole.System, wire[1].Role);
        Assert.Contains("앞부분 요약", wire[1].Content);
    }

    [Fact]
    public void BuildWire_DropsSummarizedRangeButKeepsRecent()
    {
        var s = LongSession(4, 100);          // 1 + 4*3 = 13 메시지
        s.CompactionSummary = "요약";
        s.CompactedThrough = 7;               // Messages[1..7) 대체

        var wire = ContextCompactor.BuildWireMessages(s);

        // [시스템] + [요약] + Messages[7..13) = 2 + 6
        Assert.Equal(8, wire.Count);
        Assert.DoesNotContain(wire, m => m.Content == "요청 0");   // 요약된 구간
        Assert.Contains(wire, m => m.Content == "요청 3");         // 최근 구간은 원문
    }

    [Fact]
    public void BuildWire_NeverOrphansToolResults()
    {
        var s = LongSession(6, 100);
        s.CompactionSummary = "요약";
        s.CompactedThrough = 10;

        var wire = ContextCompactor.BuildWireMessages(s);

        // 남은 tool 결과의 tool_call_id 는 모두 같은 목록의 assistant tool_calls 에 존재해야 한다.
        var emitted = wire
            .Where(m => m.ToolCalls is not null)
            .SelectMany(m => m.ToolCalls!)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        var referenced = wire.Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolCallId!);

        Assert.All(referenced, id => Assert.Contains(id, emitted));
    }

    [Fact]
    public void BuildWire_DoesNotMutateSession()
    {
        var s = LongSession(3, 100);
        var before = s.Messages.Count;

        s.CompactionSummary = "요약";
        s.CompactedThrough = 4;
        ContextCompactor.BuildWireMessages(s);

        // 원본 이력은 디스크·동기화·UI 가 보는 진실이다 — 컴팩션이 건드리면 안 된다.
        Assert.Equal(before, s.Messages.Count);
        Assert.Equal("요청 0", s.Messages[1].Content);
    }

    [Fact]
    public void BuildWire_ElidesOldToolResultsAsLastResort()
    {
        // 요약 없이도 예산을 넘으면(요약 실패 등) 오래된 도구 결과를 생략해 요청 거부를 막는다.
        var s = LongSession(6, 100_000);

        var wire = ContextCompactor.BuildWireMessages(s);

        Assert.Contains(wire, m => m.Content == "[이전 도구 결과 생략 — 컨텍스트 절약]");
        Assert.Equal(s.Messages.Count, wire.Count);   // 개수·역할·짝은 보존
    }

    // ── MaybeCompactAsync ──

    [Fact]
    public async Task Compact_NotTriggeredForShortHistory()
    {
        var api = new FakeSummarizerApi("요약본");
        var s = LongSession(2, 100);

        var outcome = await new ContextCompactor(api, new FakeSettingsService()).MaybeCompactAsync(s);

        Assert.Equal(CompactionOutcome.NotNeeded, outcome);
        Assert.Equal(0, api.CallCount);   // 평소 턴에서 모델을 부르면 안 된다
        Assert.Null(s.CompactionSummary);
    }

    [Fact]
    public async Task Compact_TriggersAndStoresSummary()
    {
        var api = new FakeSummarizerApi("앞부분 요약본");
        var s = LongSession(8, 60_000);   // ~480K 자 > 300K 예산

        var outcome = await new ContextCompactor(api, new FakeSettingsService()).MaybeCompactAsync(s);

        Assert.Equal(CompactionOutcome.Compacted, outcome);
        Assert.Equal("앞부분 요약본", s.CompactionSummary);
        Assert.True(s.CompactedThrough > 0);
        Assert.Equal(1, api.CallCount);
    }

    [Fact]
    public async Task Compact_CutLandsOnNonToolBoundary()
    {
        var api = new FakeSummarizerApi("요약");
        var s = LongSession(8, 60_000);

        await new ContextCompactor(api, new FakeSettingsService()).MaybeCompactAsync(s);

        // 절단 지점이 tool 이면 tool_use/tool_result 짝을 반토막 낸 것이다(user/assistant 는 안전).
        Assert.NotEqual(MessageRole.Tool, s.Messages[s.CompactedThrough].Role);
    }

    [Fact]
    public async Task Compact_ProducesValidUserFirstWire()
    {
        var api = new FakeSummarizerApi("요약");
        var s = LongSession(8, 60_000);
        await new ContextCompactor(api, new FakeSettingsService()).MaybeCompactAsync(s);

        var wire = ContextCompactor.BuildWireMessages(s);

        // system 프롬프트·요약 뒤 첫 비-system 메시지는 반드시 user 여야 한다(Anthropic 계약).
        var firstNonSystem = wire.First(m => m.Role != MessageRole.System);
        Assert.Equal(MessageRole.User, firstNonSystem.Role);

        // tool_result 는 모두 같은 와이어의 tool_use 와 짝이 맞아야 한다.
        var emitted = wire.Where(m => m.ToolCalls is not null).SelectMany(m => m.ToolCalls!).Select(c => c.Id).ToHashSet();
        foreach (var toolMsg in wire.Where(m => m.Role == MessageRole.Tool))
            Assert.Contains(toolMsg.ToolCallId!, emitted);
    }

    [Fact]
    public async Task Compact_SingleGoalSessionCanCompact()
    {
        // 단일 목표 세션(user 메시지 1개, 도구 왕복 다수) — 가장 압축이 필요한데 종전엔 영영 안 됐다.
        var s = new AgentSession();
        s.Messages.Add(AgentMessage.System("시스템 프롬프트"));
        s.Messages.Add(AgentMessage.User("큰 작업 하나"));   // user 는 이 하나뿐
        for (var i = 0; i < 10; i++)
        {
            s.Messages.Add(AgentMessage.Assistant(null, [new ToolCall($"c{i}", "read_file", EmptyArgs)]));
            s.Messages.Add(AgentMessage.ToolResultMsg($"c{i}", Big(60_000), isError: false));
        }

        var outcome = await new ContextCompactor(new FakeSummarizerApi("요약"), new FakeSettingsService())
            .MaybeCompactAsync(s);

        Assert.Equal(CompactionOutcome.Compacted, outcome);

        // 압축 후 와이어도 user-first + 짝 유지여야 한다(합성 user 삽입).
        var wire = ContextCompactor.BuildWireMessages(s);
        Assert.Equal(MessageRole.User, wire.First(m => m.Role != MessageRole.System).Role);
    }

    [Fact]
    public async Task Compact_SummarizerGetsNoTools()
    {
        var api = new FakeSummarizerApi("요약");
        await new ContextCompactor(api, new FakeSettingsService()).MaybeCompactAsync(LongSession(8, 60_000));

        // 도구를 주면 요약하랬더니 파일을 읽으려 든다.
        Assert.NotNull(api.LastRequest);
        Assert.Empty(api.LastRequest!.Tools);
    }

    [Fact]
    public async Task Compact_IsCachedNotRepeatedEveryTurn()
    {
        var api = new FakeSummarizerApi("요약");
        var compactor = new ContextCompactor(api, new FakeSettingsService());
        var s = LongSession(8, 60_000);

        await compactor.MaybeCompactAsync(s);
        var afterFirst = api.CallCount;

        // 이어지는 턴 — 이력이 더 늘지 않았으면 다시 요약하지 않아야 한다.
        await compactor.MaybeCompactAsync(s);

        Assert.Equal(afterFirst, api.CallCount);
    }

    [Fact]
    public async Task Compact_SecondPassIncludesPreviousSummary()
    {
        var api = new FakeSummarizerApi("요약 v2");
        var compactor = new ContextCompactor(api, new FakeSettingsService());
        var s = LongSession(8, 60_000);

        await compactor.MaybeCompactAsync(s);
        var first = s.CompactionSummary;

        // 대화가 더 길어져 재컴팩션이 필요한 상황.
        for (var i = 0; i < 8; i++)
            s.Messages.AddRange(Turn($"추가 {i}", $"more-{i}", 60_000));

        await compactor.MaybeCompactAsync(s);

        // 이전 요약을 입력에 포함해야 누적 요약이 된다 — 안 그러면 오래된 맥락이 영영 사라진다.
        Assert.Contains(first!, api.LastTranscript!);
    }

    [Fact]
    public async Task Compact_FailureIsNotFatal()
    {
        var s = LongSession(8, 60_000);

        var outcome = await new ContextCompactor(new FakeSummarizerApi(throws: true), new FakeSettingsService())
            .MaybeCompactAsync(s);

        Assert.Equal(CompactionOutcome.Failed, outcome);
        Assert.Null(s.CompactionSummary);   // 실패했으면 상태를 바꾸지 않는다

        // 폴백이 동작해 요청은 여전히 만들 수 있어야 한다.
        Assert.NotEmpty(ContextCompactor.BuildWireMessages(s));
    }

    [Fact]
    public async Task Compact_EmptySummaryIsTreatedAsFailure()
    {
        var s = LongSession(8, 60_000);

        var outcome = await new ContextCompactor(new FakeSummarizerApi("   "), new FakeSettingsService())
            .MaybeCompactAsync(s);

        Assert.Equal(CompactionOutcome.Failed, outcome);
        Assert.Null(s.CompactionSummary);
    }

    [Fact]
    public async Task Compact_HandlesEmptySession()
    {
        var outcome = await new ContextCompactor(new FakeSummarizerApi("x"), new FakeSettingsService())
            .MaybeCompactAsync(new AgentSession());

        Assert.Equal(CompactionOutcome.NotNeeded, outcome);
    }
}

/// <summary>요약 응답만 흉내내는 API 스텁.</summary>
internal sealed class FakeSummarizerApi : StubAgentApi
{
    private readonly string _summary;
    private readonly bool _throws;

    public int CallCount { get; private set; }
    public AgentRequest? LastRequest { get; private set; }
    public string? LastTranscript { get; private set; }

    public FakeSummarizerApi(string summary = "요약", bool throws = false)
    {
        _summary = summary;
        _throws = throws;
    }

    public override async IAsyncEnumerable<AgentStreamEvent> SendAsync(
        AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        CallCount++;
        LastRequest = request;
        LastTranscript = request.Messages.LastOrDefault()?.Content;

        if (_throws)
            throw new AgentException("요약 서버 오류");

        yield return new ContentDelta(_summary);
        yield return new MessageStop("end_turn", new Usage(0, 0, 0));
        await Task.CompletedTask;
    }
}
