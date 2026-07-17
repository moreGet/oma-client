using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class AgentOrchestrator(
    IAgentApiClient api,
    IToolRegistry tools,
    IPermissionService permissions,
    IWorkspaceContext workspace,
    ISettingsService settings,
    IToolPolicyService policy) : IAgentOrchestrator
{
    // 서버엔 깔끔한 SemVer 를 전송한다(빌드 메타·해시 제외).
    private static readonly string ClientVersion = AppVersion.Semantic;

    /// <summary>
    /// turn 당 모델 출력 상한(와이어 max_tokens).
    ///
    /// v5 에서 사용자 설정을 없애며 "서버 제어" 전제로 상수를 보내기로 했으나, 확인해 보니
    /// 서버는 이 값에 상한을 두지 않고 그대로 전달만 한다(어댑터는 미지정 시에만 자체 기본값 사용).
    /// 즉 실질적으로 이 상수가 유일한 통제점이므로, 4096 → 8192 로 올린다.
    /// 4096 은 긴 최종 요약이 잘리기 쉬운 값이었고, 잘려도 조용히 완료 처리돼 사용자가 알 수 없었다
    /// (아래 max_tokens stop_reason 처리 참조).
    ///
    /// 주의: 서버의 기본 모델(claude-3-5-sonnet-latest)은 8192 를 지원하지만, 출력 상한이 4096 인
    /// 모델(구 GPT-4 등)로 프로바이더를 바꾸면 프로바이더가 요청을 거부할 수 있다.
    /// 모델별 상한을 알아서 맞추려면 서버가 clamp 하는 편이 옳다(별도 과제).
    /// </summary>
    private const int DefaultMaxTokens = 8192;

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string userGoal,
        AgentSession session,
        IReadOnlyList<Attachment>? attachments = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var mode = settings.Current.PermissionMode;

        // 1) 시스템 프롬프트 시드.
        if (session.Messages.Count == 0)
            session.Messages.Add(AgentMessage.System(
                AgentSession.DefaultSystemPrompt(workspace.Root, mode, workspace.Roots)));

        // 2) 사용자 목표 추가(첨부가 있으면 함께 싣는다).
        session.Messages.Add(AgentMessage.User(userGoal, attachments));

        var max = settings.Current.MaxIterations;
        var iteration = 0;

        while (iteration < max && !ct.IsCancellationRequested)
        {
            yield return new AgentIterationAdvanced(iteration + 1, max);

            var request = BuildRequest(session);

            var assistantText = new StringBuilder();
            var pendingCalls = new List<ToolCall>();
            string? stopReason = null;
            ErrorEvent? streamError = null;

            // a) 모델 응답 스트리밍.
            await foreach (var signal in SafeStream(request, ct).ConfigureAwait(false))
            {
                if (signal is StreamFailure f)
                {
                    streamError = new ErrorEvent(f.Code, f.Message);
                    break;
                }

                var evt = ((StreamItem)signal).Event;
                if (evt is ContentDelta cd)
                {
                    assistantText.Append(cd.Text);
                    yield return new AgentTextDelta(cd.Text);
                }
                else if (evt is ToolCallEvent tce)
                {
                    pendingCalls.Add(new ToolCall(tce.Id, tce.Name, tce.Args));
                }
                else if (evt is MessageStop ms)
                {
                    stopReason = ms.StopReason;
                    session.LastUsage = ms.Usage;
                }
                else if (evt is ErrorEvent ee)
                {
                    streamError = ee;
                    break;
                }
                // MessageStart 무시.
            }

            if (streamError is not null)
            {
                yield return new AgentError(streamError.Code, streamError.Message);
                yield break;
            }

            if (ct.IsCancellationRequested)
            {
                yield return new AgentError("cancelled", "사용자가 중지했습니다.");
                yield break;
            }

            // d) assistant 턴 기록.
            var calls = pendingCalls.Count > 0 ? pendingCalls : null;
            var text = assistantText.ToString();
            session.Messages.Add(AgentMessage.Assistant(text, calls));
            yield return new AgentAssistantMessageComplete(text);

            // e) tool_use 가 아니면 완료.
            var isToolUse = string.Equals(stopReason, "tool_use", StringComparison.OrdinalIgnoreCase)
                            || calls is not null;
            if (!isToolUse)
            {
                // max_tokens 로 끊긴 응답도 여기로 온다. 조용히 완료 처리하면 사용자는 답이 잘린 줄
                // 모른 채 불완전한 내용을 신뢰하게 된다 — 완료 전에 사실을 알린다.
                if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
                    yield return new AgentError("max_tokens",
                        $"모델 응답이 turn 당 최대 길이({DefaultMaxTokens} 토큰)에 도달해 잘렸습니다. " +
                        "이어서 작성해 달라고 요청하거나, 작업을 더 작게 나눠 주세요.");

                yield return new AgentDone(text, session.LastUsage);
                yield break;
            }

            // f) 각 도구 실행.
            foreach (var call in pendingCalls)
            {
                if (ct.IsCancellationRequested)
                {
                    yield return new AgentError("cancelled", "사용자가 중지했습니다.");
                    yield break;
                }

                if (!tools.TryGet(call.Name, out var tool))
                {
                    var msg = $"Unknown tool: {call.Name}";
                    session.Messages.Add(AgentMessage.ToolResultMsg(call.Id, msg, isError: true));
                    yield return new AgentToolCallResult(call.Id, call.Name, ToolResult.Fail(msg));
                    continue;
                }

                // 서버 도구 정책 게이트(로컬 권한 게이트·샌드박스보다 앞단).
                var gate = await policy.EvaluateAsync(call.Name, call.Arguments, ct).ConfigureAwait(false);
                if (!gate.Allowed)
                {
                    var msg = $"서버 도구 정책에 의해 차단됨: {gate.Reason ?? call.Name}";
                    session.Messages.Add(AgentMessage.ToolResultMsg(call.Id, msg, isError: true));
                    yield return new AgentToolCallResult(call.Id, call.Name, ToolResult.Fail(msg));
                    continue;  // 승인 카드·실행 건너뜀, 모델엔 차단 사유 피드백
                }

                var risk = tool.Risk;
                yield return new AgentToolCallStarted(call.Id, call.Name, call.Arguments, risk);

                var gated = mode != PermissionMode.FullAuto && risk != ToolRisk.ReadOnly;
                if (gated)
                    yield return new AgentAwaitingApproval(call.Id, call.Name, call.Arguments, risk);

                var ctx = new ToolContext(workspace, settings.Current.PermissionMode);

                var (result, cancelled) = await ExecuteCallAsync(tool, call, risk, ctx, ct)
                    .ConfigureAwait(false);

                if (cancelled)
                {
                    yield return new AgentError("cancelled", "사용자가 중지했습니다.");
                    yield break;
                }

                session.Messages.Add(AgentMessage.ToolResultMsg(call.Id, result.Content, result.IsError));
                yield return new AgentToolCallResult(call.Id, call.Name, result);
            }

            iteration++;
        }

        if (ct.IsCancellationRequested)
        {
            yield return new AgentError("cancelled", "사용자가 중지했습니다.");
            yield break;
        }

        if (iteration >= max)
            yield return new AgentError("max_iterations", "최대 반복 횟수에 도달했습니다.");
    }

    private AgentRequest BuildRequest(AgentSession session)
    {
        var s = settings.Current;
        // 서버 정책상 노출(exposed) 도구만 모델에 전달한다(비활성 도구는 모델이 아예 못 봄).
        // 미로드/realtime이면 IsExposed가 전체 true → 현행과 동일.
        var exposedTools = tools.ToSchemas().Where(t => policy.IsExposed(t.Name)).ToList();
        return new AgentRequest(
            Model: s.ModelId,
            Stream: true,
            MaxTokens: DefaultMaxTokens,
            Messages: WindowMessages(session.Messages),
            Tools: exposedTools,
            Metadata: new RequestMetadata("windows", workspace.Root, ClientVersion));
    }

    // 히스토리 예산(문자). 평소엔 미발동 — 초과 시에만 오래된 tool 결과를 요약 대체해 컨텍스트/토큰 폭증을 막는다.
    private const int HistoryCharBudget = 300_000;
    private const string ElidedToolResult = "[이전 도구 결과 생략 — 컨텍스트 절약]";

    /// <summary>
    /// 최신에서부터 누적 크기를 재고 예산을 넘긴 뒤의 '오래된' tool 결과 content 만 플레이스홀더로 대체한다.
    /// 메시지 개수·역할·tool_call_id 페어링과 assistant/user 메시지는 그대로 보존(서버 계약 유지). session.Messages 는 불변(영속 원본 보존).
    /// </summary>
    private static List<AgentMessage> WindowMessages(IReadOnlyList<AgentMessage> all)
    {
        var running = 0;
        var elide = false;
        var result = new AgentMessage[all.Count];
        for (var i = all.Count - 1; i >= 0; i--)
        {
            var m = all[i];
            running += m.Content?.Length ?? 0;
            if (!elide && running > HistoryCharBudget) elide = true;

            result[i] = (elide && m.Role == MessageRole.Tool && (m.Content?.Length ?? 0) > ElidedToolResult.Length)
                ? m with { Content = ElidedToolResult }
                : m;
        }
        return new List<AgentMessage>(result);
    }

    // R3(설계 의도): 도구 예외는 여기서 중앙집중으로 ToolResult.Fail(is_error) 변환 — 모든 도구가
    // 동일 오류계약을 따르고 정상 경로만 구현. 도구별 try/catch 는 의도적으로 두지 않는다.
    private async Task<(ToolResult Result, bool Cancelled)> ExecuteCallAsync(
        ITool tool, ToolCall call, ToolRisk risk, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var decision = await permissions.RequestAsync(call, risk, ctx, ct).ConfigureAwait(false);
            if (decision == PermissionDecision.Deny)
                return (ToolResult.Fail("Denied by user"), false);

            var result = await tool.ExecuteAsync(call.Arguments, ctx, ct).ConfigureAwait(false);
            return (result, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (ToolResult.Fail("사용자가 중지했습니다."), true);
        }
        catch (AgentException ex)
        {
            return (ToolResult.Fail(ex.Message), false);
        }
        catch (Exception ex)
        {
            return (ToolResult.Fail($"도구 실행 오류: {ex.Message}"), false);
        }
    }

    // ── SSE 스트림 예외-안전 래퍼 (yield 내부 try/catch 제약 회피) ───────
    private abstract record StreamSignal;
    private sealed record StreamItem(AgentStreamEvent Event) : StreamSignal;
    private sealed record StreamFailure(string Code, string Message) : StreamSignal;

    private async IAsyncEnumerable<StreamSignal> SafeStream(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        IAsyncEnumerator<AgentStreamEvent> enumerator;
        string? connectFailure = null;
        try
        {
            enumerator = api.SendAsync(request, ct).GetAsyncEnumerator(ct);
        }
        catch (AgentException ex)
        {
            connectFailure = ex.Message;
            enumerator = null!;
        }

        if (connectFailure is not null)
        {
            yield return new StreamFailure("connection", connectFailure);
            yield break;
        }

        try
        {
            while (true)
            {
                StreamSignal signal;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        yield break;
                    signal = new StreamItem(enumerator.Current);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }
                catch (AgentException ex)
                {
                    signal = new StreamFailure("connection", ex.Message);
                }
                catch (Exception ex)
                {
                    signal = new StreamFailure("stream_error", ex.Message);
                }

                yield return signal;
                if (signal is StreamFailure)
                    yield break;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
