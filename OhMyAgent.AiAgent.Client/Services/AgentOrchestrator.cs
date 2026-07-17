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
    IToolPolicyService policy,
    ContextCompactor compactor) : IAgentOrchestrator
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
        [EnumeratorCancellation] CancellationToken ct = default,
        int? maxIterations = null)
    {
        var mode = settings.Current.PermissionMode;

        // 1) 시스템 프롬프트 시드 — 비어 있을 때만.
        //    호출자가 미리 넣어 둔 프롬프트(서브에이전트 전용 등)나 복원된 세션을 덮어쓰지 않는다.
        if (session.Messages.Count == 0)
            session.Messages.Add(AgentMessage.System(
                AgentSession.DefaultSystemPrompt(workspace.Root, mode, workspace.Roots)));

        // 2) 사용자 목표 추가(첨부가 있으면 함께 싣는다).
        session.Messages.Add(AgentMessage.User(userGoal, attachments));

        var max = maxIterations ?? settings.Current.MaxIterations;
        var iteration = 0;

        while (iteration < max && !ct.IsCancellationRequested)
        {
            yield return new AgentIterationAdvanced(iteration + 1, max);

            // 요청을 만들기 전에 이력이 예산을 넘었는지 확인한다. 대부분의 턴은 NotNeeded 로 즉시 반환된다
            // (모델 호출 없음). 실패해도 진행한다 — BuildWireMessages 가 생략 폴백으로 방어한다.
            var compaction = await compactor.MaybeCompactAsync(session, ct).ConfigureAwait(false);
            if (compaction == CompactionOutcome.Compacted)
                yield return new AgentNotice(
                    "대화가 길어져 앞부분을 요약해 압축했습니다. 이전 내용의 세부는 요약본으로 대체됩니다.");

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
                    yield return new AgentNotice(
                        $"⚠ 모델 응답이 turn 당 최대 길이({DefaultMaxTokens} 토큰)에 도달해 잘렸습니다. " +
                        "이어서 작성해 달라고 요청하거나, 작업을 더 작게 나눠 주세요.");

                yield return new AgentDone(text, session.LastUsage);
                yield break;
            }

            // f) 도구 실행 — 배치가 전부 ReadOnly 면 병렬, 아니면 순차.
            //
            // 병렬 조건을 ReadOnly 전용으로 좁힌 근거(둘 다 코드로 확인된 사실이다):
            //  - 승인: PermissionService.RequestAsync 는 ReadOnly 를 모드와 무관하게 항상 자동 허용한다.
            //    즉 승인 핸들러(_handler, UI 대화상자)를 아예 타지 않으므로 동시 승인 요청이 겹칠 수 없다.
            //    쓰기 도구가 섞이면 승인 대화상자가 동시에 여러 개 뜨거나 서로를 덮어쓴다.
            //  - 부작용: ReadOnly 는 상태를 바꾸지 않으므로 같은 파일에 대한 쓰기-쓰기/쓰기-읽기 경합이 없다.
            // 하나라도 쓰기/실행이 섞이면 배치 전체를 순차로 돌린다 — 부분 병렬은 모델이 의도한
            // 호출 순서를 뒤집을 수 있어 이득보다 위험이 크다.
            var parallelizable = pendingCalls.Count > 1
                && pendingCalls.All(c => tools.TryGet(c.Name, out var t) && t.Risk == ToolRisk.ReadOnly);

            if (parallelizable)
            {
                // 시작 이벤트는 순서대로 한 번에 — UI 가 N개가 동시에 도는 것을 보여준다.
                foreach (var call in pendingCalls)
                    yield return new AgentToolCallStarted(call.Id, call.Name, call.Arguments, ToolRisk.ReadOnly);

                var results = new ToolResult[pendingCalls.Count];
                var cancelledByUser = false;
                var running = new List<Task<ParallelCallOutcome>>(pendingCalls.Count);

                using var slots = new SemaphoreSlim(MaxParallelToolCalls);
                for (var i = 0; i < pendingCalls.Count; i++)
                    running.Add(RunCallAsync(i, pendingCalls[i], slots, ct));

                // 완료되는 대로 결과를 방출한다(느린 하나가 나머지 표시를 막지 않게).
                while (running.Count > 0)
                {
                    var finished = await Task.WhenAny(running).ConfigureAwait(false);
                    running.Remove(finished);

                    var outcome = await finished.ConfigureAwait(false);   // RunCallAsync 는 던지지 않는다
                    if (outcome.Cancelled)
                    {
                        cancelledByUser = true;
                        continue;   // 같은 ct 를 공유하므로 나머지도 곧 취소된다
                    }

                    results[outcome.Index] = outcome.Result;
                    var call = pendingCalls[outcome.Index];
                    yield return new AgentToolCallResult(call.Id, call.Name, outcome.Result);
                }

                if (cancelledByUser)
                {
                    yield return new AgentError("cancelled", "사용자가 중지했습니다.");
                    yield break;
                }

                // 이력에는 "완료 순서"가 아니라 "모델이 호출한 순서"로 넣는다.
                // tool_result 는 assistant 의 tool_use 순서와 짝을 이뤄야 하고, 완료 순서로 쓰면
                // 매 실행마다 대화 이력이 달라져 프롬프트 캐시도 무의미해진다.
                for (var i = 0; i < pendingCalls.Count; i++)
                    session.Messages.Add(AgentMessage.ToolResultMsg(
                        pendingCalls[i].Id, results[i].Content, results[i].IsError));

                iteration++;
                continue;
            }

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

    /// <summary>병렬 도구 호출 동시 실행 상한. task(서브에이전트) 배치는 하나하나가 LLM 루프라 무제한은 위험하다.</summary>
    private const int MaxParallelToolCalls = 8;

    /// <summary>병렬 실행 결과 — 호출 순서(<see cref="Index"/>)를 들고 다녀야 이력을 원래 순서로 되돌릴 수 있다.</summary>
    private readonly record struct ParallelCallOutcome(int Index, ToolResult Result, bool Cancelled);

    /// <summary>
    /// 도구 하나를 실행한다(병렬 경로 전용). <b>절대 예외를 던지지 않는다</b> —
    /// 던지면 Task.WhenAny 로 회수할 때 이터레이터 밖으로 튀어 루프 전체가 죽는다.
    /// </summary>
    private async Task<ParallelCallOutcome> RunCallAsync(int index, ToolCall call, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ParallelCallOutcome(index, ToolResult.Fail("사용자가 중지했습니다."), true);
        }

        try
        {
            // 서버 도구 정책 게이트 — 순차 경로와 동일하게 실행 직전에 평가한다.
            var gate = await policy.EvaluateAsync(call.Name, call.Arguments, ct).ConfigureAwait(false);
            if (!gate.Allowed)
            {
                return new ParallelCallOutcome(index,
                    ToolResult.Fail($"서버 도구 정책에 의해 차단됨: {gate.Reason ?? call.Name}"), false);
            }

            if (!tools.TryGet(call.Name, out var tool))
                return new ParallelCallOutcome(index, ToolResult.Fail($"Unknown tool: {call.Name}"), false);

            var ctx = new ToolContext(workspace, settings.Current.PermissionMode);
            var (result, cancelled) = await ExecuteCallAsync(tool, call, tool.Risk, ctx, ct).ConfigureAwait(false);
            return new ParallelCallOutcome(index, result, cancelled);
        }
        catch (OperationCanceledException)
        {
            return new ParallelCallOutcome(index, ToolResult.Fail("사용자가 중지했습니다."), true);
        }
        catch (Exception ex)
        {
            // ExecuteCallAsync 가 도구 예외를 이미 흡수하므로 여기 오는 건 정책 게이트 등 그 바깥이다.
            AppLog.Warn("AgentOrchestrator", $"병렬 도구 실행 실패: {call.Name}", ex);
            return new ParallelCallOutcome(index, ToolResult.Fail($"도구 실행 오류: {ex.Message}"), false);
        }
        finally
        {
            slots.Release();
        }
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
            Messages: ContextCompactor.BuildWireMessages(session),
            Tools: exposedTools,
            Metadata: new RequestMetadata("windows", workspace.Root, ClientVersion));
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
