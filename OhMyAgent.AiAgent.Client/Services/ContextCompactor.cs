using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>컴팩션 시도 결과.</summary>
public enum CompactionOutcome
{
    /// <summary>예산 안 — 아무것도 하지 않음(대부분의 턴).</summary>
    NotNeeded,

    /// <summary>요약 성공 — 세션의 컴팩션 상태가 갱신됨.</summary>
    Compacted,

    /// <summary>요약 실패 — 폴백(오래된 도구 결과 생략)으로 진행해야 함.</summary>
    Failed
}

/// <summary>
/// 대화 이력을 예산 안으로 유지한다.
///
/// 종전 방식의 문제: 예산을 넘기면 오래된 도구 결과를 "[이전 도구 결과 생략]" 으로 <b>버렸다</b>.
/// 자리는 절약되지만 내용이 사라져, 모델이 "아까 그 파일에 뭐가 있었지"를 잃고 같은 파일을 다시 읽는다.
/// 긴 세션일수록 같은 일을 반복하게 된다.
///
/// 대신 요약한다: 오래된 구간을 모델에게 요약시켜 한 덩어리로 압축하고, 최근 대화는 원문 그대로 둔다.
///
/// 설계상 중요한 점 셋:
///  1. <see cref="AgentSession.Messages"/> 는 절대 건드리지 않는다. 컴팩션은 "와이어 투영"에만 적용된다 —
///     디스크·서버 동기화에 남는 이력은 온전하다. 세션에는 요약과 절단 지점만 기록한다.
///  2. 결과를 세션에 캐시한다. 매 턴 다시 요약하면 컴팩션이 절약한 비용보다 요약 비용이 커진다.
///  3. 절단은 user 메시지 경계에서만 한다. assistant(tool_calls) 와 그에 딸린 tool 결과 사이를 자르면
///     tool_call_id 짝이 깨져 서버/프로바이더가 요청을 거부한다.
/// </summary>
public sealed class ContextCompactor(IAgentApiClient api, ISettingsService settings)
{
    /// <summary>이 크기를 넘으면 컴팩션을 시도한다(문자 수).</summary>
    public const int HistoryCharBudget = 300_000;

    /// <summary>컴팩션 후 원문으로 남길 최근 대화의 목표 크기. 낮을수록 요약이 잦아지고 맥락이 얕아진다.</summary>
    private const int KeepCharTarget = 120_000;

    /// <summary>요약문 자체의 상한. 요약이 길면 압축이 아니다.</summary>
    private const int SummaryMaxTokens = 2048;

    /// <summary>요약 대상 원문이 이보다 짧으면 요약할 가치가 없다(모델 호출 비용만 든다).</summary>
    private const int MinCharsWorthSummarizing = 20_000;

    /// <summary>
    /// 필요하면 오래된 구간을 요약해 세션의 컴팩션 상태를 갱신한다.
    /// 실패해도 예외를 던지지 않는다 — 컴팩션 실패가 사용자의 대화를 막을 이유는 없다(폴백은 호출자 몫).
    /// </summary>
    public async Task<CompactionOutcome> MaybeCompactAsync(AgentSession session, CancellationToken ct = default)
    {
        var messages = session.Messages;
        if (messages.Count == 0) return CompactionOutcome.NotNeeded;

        if (MeasureWire(session) <= HistoryCharBudget)
            return CompactionOutcome.NotNeeded;

        var cut = FindCutIndex(messages, session.CompactedThrough);
        if (cut is null)
            return CompactionOutcome.Failed;   // 자를 수 있는 user 경계가 없음(예: 단일 거대 턴)

        var transcript = RenderForSummary(session, cut.Value);
        if (transcript.Length < MinCharsWorthSummarizing)
            return CompactionOutcome.NotNeeded;

        string summary;
        try
        {
            summary = await SummarizeAsync(transcript, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ContextCompactor", "이력 요약 실패 — 생략 폴백으로 진행합니다.", ex);
            return CompactionOutcome.Failed;
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            AppLog.Warn("ContextCompactor", "이력 요약이 비어 있어 생략 폴백으로 진행합니다.");
            return CompactionOutcome.Failed;
        }

        session.CompactionSummary = summary.Trim();
        session.CompactedThrough = cut.Value;

        AppLog.Info("ContextCompactor",
            $"이력 컴팩션: {cut.Value}개 메시지 → 요약 {summary.Length:N0}자 (세션 {session.Id}).");

        return CompactionOutcome.Compacted;
    }

    /// <summary>
    /// 서버로 보낼 메시지 목록을 만든다. 순수 함수 — 세션을 바꾸지 않는다.
    ///
    /// 구성: [시스템 프롬프트] + [요약(system)] + [절단 이후 원문]
    /// 요약을 <b>system</b> 역할로 넣는 이유: Claude 어댑터는 모든 system 메시지를 top-level System 파라미터로
    /// 합치므로 메시지 시퀀스에 끼어들지 않는다 → tool_call_id 짝을 깨뜨릴 수 없다. user 로 넣으면
    /// 절단 직후의 user 메시지와 연속 user 가 되어 프로바이더별로 병합/거부 위험이 있다.
    /// </summary>
    public static List<AgentMessage> BuildWireMessages(AgentSession session)
    {
        var all = session.Messages;
        if (all.Count == 0) return [];

        var wire = new List<AgentMessage>(all.Count + 1) { all[0] };   // [0] = 시스템 프롬프트

        if (!string.IsNullOrWhiteSpace(session.CompactionSummary))
            wire.Add(AgentMessage.System(
                "다음은 이 대화의 앞부분을 요약한 것이다. 원문은 컨텍스트 절약을 위해 생략되었다.\n\n" +
                session.CompactionSummary));

        var start = Math.Max(1, session.CompactedThrough);
        for (var i = start; i < all.Count; i++)
            wire.Add(all[i]);

        // 최후의 안전망: 요약을 했는데도(또는 요약이 실패했는데도) 여전히 예산을 넘으면
        // 종전 방식대로 오래된 도구 결과를 생략한다. 정보는 잃지만 요청이 거부되는 것보단 낫다.
        return ElideOldToolResults(wire);
    }

    // ── 내부 ──

    private const string ElidedToolResult = "[이전 도구 결과 생략 — 컨텍스트 절약]";

    /// <summary>
    /// 최신에서부터 누적 크기를 재고, 예산을 넘긴 뒤의 '오래된' tool 결과 content 만 플레이스홀더로 대체한다.
    /// 메시지 개수·역할·tool_call_id 짝은 그대로 보존한다(서버 계약 유지).
    /// </summary>
    private static List<AgentMessage> ElideOldToolResults(List<AgentMessage> wire)
    {
        var running = 0;
        var elide = false;
        var result = new AgentMessage[wire.Count];

        for (var i = wire.Count - 1; i >= 0; i--)
        {
            var m = wire[i];
            running += m.Content?.Length ?? 0;
            if (!elide && running > HistoryCharBudget) elide = true;

            result[i] = (elide && m.Role == MessageRole.Tool && (m.Content?.Length ?? 0) > ElidedToolResult.Length)
                ? m with { Content = ElidedToolResult }
                : m;
        }

        return [.. result];
    }

    private static int MeasureWire(AgentSession session)
    {
        var total = session.CompactionSummary?.Length ?? 0;
        var start = Math.Max(1, session.CompactedThrough);

        for (var i = start; i < session.Messages.Count; i++)
            total += Measure(session.Messages[i]);

        return total;
    }

    private static int Measure(AgentMessage m)
    {
        var n = m.Content?.Length ?? 0;
        if (m.ToolCalls is { } calls)
            foreach (var c in calls)
                n += c.Name.Length + c.Arguments.GetRawText().Length;
        return n;
    }

    /// <summary>
    /// 최근 <see cref="KeepCharTarget"/> 만큼을 원문으로 남기는 절단 지점을 찾는다.
    /// user 메시지 경계만 허용한다 — assistant(tool_calls)/tool 결과 사이를 자르면 짝이 깨진다.
    /// 자를 곳이 없으면 null.
    /// </summary>
    private static int? FindCutIndex(IReadOnlyList<AgentMessage> messages, int currentCut)
    {
        var kept = 0;
        int? candidate = null;

        for (var i = messages.Count - 1; i > Math.Max(0, currentCut); i--)
        {
            kept += Measure(messages[i]);

            // 최근 구간을 충분히 확보한 뒤 처음 만나는 user 경계에서 자른다.
            if (kept >= KeepCharTarget && messages[i].Role == MessageRole.User)
            {
                candidate = i;
                break;
            }
        }

        // 이번에 자를 지점이 이전 절단보다 뒤여야 실제로 줄어든다.
        return candidate > currentCut ? candidate : null;
    }

    /// <summary>요약 대상 구간(이전 요약 포함)을 하나의 텍스트로 펼친다.</summary>
    private static string RenderForSummary(AgentSession session, int cut)
    {
        var sb = new StringBuilder();

        // 이전 요약을 함께 넣어 누적 요약이 되게 한다 — 안 그러면 컴팩션할 때마다 더 오래된 맥락이 사라진다.
        if (!string.IsNullOrWhiteSpace(session.CompactionSummary))
            sb.AppendLine("## 이전 요약").AppendLine(session.CompactionSummary).AppendLine();

        sb.AppendLine("## 대화 원문");

        var start = Math.Max(1, session.CompactedThrough);
        for (var i = start; i < cut; i++)
        {
            var m = session.Messages[i];
            var role = m.Role switch
            {
                MessageRole.User => "사용자",
                MessageRole.Assistant => "어시스턴트",
                MessageRole.Tool => "도구 결과",
                _ => m.Role.ToString()
            };

            sb.Append("### ").AppendLine(role);

            if (!string.IsNullOrEmpty(m.Content))
                sb.AppendLine(m.Content);

            if (m.ToolCalls is { Count: > 0 } calls)
                foreach (var c in calls)
                    sb.AppendLine($"[도구 호출: {c.Name} {c.Arguments.GetRawText()}]");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<string> SummarizeAsync(string transcript, CancellationToken ct)
    {
        var request = new AgentRequest(
            Model: settings.Current.ModelId,
            Stream: true,
            MaxTokens: SummaryMaxTokens,
            Messages: [AgentMessage.System(SummarizerPrompt), AgentMessage.User(transcript)],
            Tools: [],   // 요약에 도구는 필요 없다. 넣으면 모델이 도구를 부르려 든다.
            Metadata: new RequestMetadata("windows", string.Empty, AppVersion.Semantic));

        var sb = new StringBuilder();

        await foreach (var evt in api.SendAsync(request, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case ContentDelta d:
                    sb.Append(d.Text);
                    break;
                case ErrorEvent e:
                    throw new AgentException($"요약 요청 실패: [{e.Code}] {e.Message}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 요약 프롬프트. 요약이 나쁘면 컴팩션은 그냥 정보 손실이므로, 무엇을 반드시 남길지 명시한다.
    /// </summary>
    private const string SummarizerPrompt =
        """
        You compact an AI agent's conversation history so the agent can keep working after older turns are dropped.
        The agent will see ONLY your summary in place of the original turns. Anything you omit is permanently lost to it.

        Preserve, concretely and specifically:
        - The user's goal(s), and any constraint, preference, or correction they stated. Quote exact wording for
          instructions ("반드시 X 하라") — paraphrasing loses force.
        - Decisions made and the reason for each. "왜" matters more than "무엇" — the agent can re-read a file,
          but it cannot re-derive why an approach was rejected.
        - Facts discovered that would be expensive to rediscover: file paths, symbol names, line numbers, API shapes,
          error messages, command outputs that mattered.
        - What has actually been done so far (files created/modified, commands run) versus what is still pending.
        - Anything that failed, and how — so the agent does not repeat it.
        - Open questions and unresolved ambiguity.

        Drop: pleasantries, restatements, verbose tool output whose conclusion you already captured, and your own
        commentary about summarizing.

        Rules:
        - Be specific. "설정 파일을 수정했다" is useless; "SettingsService.cs:130 의 WriteAllText 를 tmp+Move 로 교체" is useful.
        - Never invent. If something is unclear from the transcript, say it is unclear rather than smoothing it over.
        - Do not address anyone. Write dense notes for the agent, not prose for a reader.
        - Write in Korean. Use short headed sections and bullets.
        """;
}
