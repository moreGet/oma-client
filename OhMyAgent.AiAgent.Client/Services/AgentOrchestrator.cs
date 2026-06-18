using System;
using System.Collections.Generic;
using System.Reflection;
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
    ISettingsService settings) : IAgentOrchestrator
{
    private static readonly string ClientVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string userGoal,
        AgentSession session,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var mode = settings.Current.PermissionMode;

        // 1) 시스템 프롬프트 시드.
        if (session.Messages.Count == 0)
            session.Messages.Add(AgentMessage.System(
                AgentSession.DefaultSystemPrompt(workspace.Root, mode)));

        // 2) 사용자 목표 추가.
        session.Messages.Add(AgentMessage.User(userGoal));

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
        return new AgentRequest(
            Model: s.ModelId,
            Stream: true,
            MaxTokens: s.MaxTokens,
            Messages: new List<AgentMessage>(session.Messages),
            Tools: tools.ToSchemas(),
            Metadata: new RequestMetadata("windows", workspace.Root, ClientVersion));
    }

    // R3(설계 의도): 도구 예외는 개별 ITool 이 아니라 여기서 중앙 집중적으로
    // ToolResult.Fail(is_error) 로 변환한다. 이렇게 하면 모든 도구가 동일한 오류
    // 계약을 따르고(크래시 없이 모델에 에러를 피드백), 개별 도구는 정상 경로만
    // 구현하면 된다. 도구별 try/catch 는 의도적으로 두지 않는다.
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
