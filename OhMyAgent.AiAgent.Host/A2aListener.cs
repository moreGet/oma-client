using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// A2A 서버 트랜스포트(스펙 §1-D). BCL HttpListener 로 SSE 응답을 내보내며 기존 /api/v1/agent/chat
/// 계약을 대칭 재사용한다(호출자는 AgentApiClient 무수정). 라우팅·인증·홉·동시성 게이트를 거쳐 요청별
/// new AgentSession 으로 orchestrator 를 끝까지 돌리고(자기 도구 루프) 최종 프로즈만 스트리밍한다.
/// </summary>
public sealed class A2aListener(
    IAgentOrchestrator orchestrator, A2aOptions opts, A2aInboundAuthenticator authn,
    AuthFailureReporter authFailures)
{
    public async Task RunAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(opts.Prefix);   // '/' 로 끝나야 함(A2aOptions 가 정규화)
        listener.Start();
        AppLog.Info("A2A",
            $"수신 대기: {opts.Prefix} (max_concurrency={opts.MaxConcurrency}, max_hops={opts.MaxHops}, " +
            $"anon={opts.AllowAnonymous})");

        using var gate = new SemaphoreSlim(opts.MaxConcurrency);
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Warn("A2A", $"수신 실패: {ex.Message}");
                continue;
            }

            // 요청별 백그라운드 처리 — 응답 스트림은 요청 내부에서 완결(예외는 내부 흡수).
            _ = HandleAsync(ctx, gate, ct);
        }

        try { listener.Stop(); } catch { /* 이미 정지 */ }
    }

    private async Task HandleAsync(HttpListenerContext ctx, SemaphoreSlim gate, CancellationToken ct)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            // 1) 라우팅 — /health(Public) 와 /agent/chat.
            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/api/v1/health")
            {
                await WriteJsonAsync(resp, 200, """{"status":"ok"}""", ct).ConfigureAwait(false);
                return;
            }

            if (!(req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/api/v1/agent/chat"))
            {
                await WriteErrorAsync(resp, 404, "not_found", "지원하지 않는 엔드포인트", ct).ConfigureAwait(false);
                return;
            }

            // 2) 인증(§1-E, §C-2 모드 분기). broker=서버 공개키 검증, token=공유토큰, anon=무인증.
            if (!await authn.AuthorizeAsync(req.Headers["Authorization"], ct).ConfigureAwait(false))
            {
                await WriteErrorAsync(resp, 401, "unauthorized", "유효하지 않은 A2A 토큰", ct).ConfigureAwait(false);
                return;
            }

            // 3) 홉 카운트(§2-F).
            var hop = A2aHop.Read(req.Headers[A2aHop.HeaderName]);
            if (A2aHop.ExceedsLimit(hop, opts.MaxHops))
            {
                await WriteErrorAsync(resp, 508, "loop_detected",
                    $"A2A 홉 한계({opts.MaxHops}) 초과", ct).ConfigureAwait(false);
                return;
            }

            // 4) 동시성 상한 — 초과 시 429(무한 대기 대신 백프레셔).
            if (!await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            {
                await WriteErrorAsync(resp, 429, "busy", "동시 요청 한계 초과", ct).ConfigureAwait(false);
                return;
            }

            try
            {
                // 5) 본문 → 프롬프트(§1-F).
                var prompt = await A2aRequest.ReadPromptAsync(req.InputStream, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    await WriteErrorAsync(resp, 400, "bad_request", "빈 요청", ct).ConfigureAwait(false);
                    return;
                }

                // 6) SSE 셋업 + 요청별 세션 격리 실행.
                A2aSseWriter.PrepareResponse(resp);
                var writer = new A2aSseWriter(resp.OutputStream,
                    runId: Guid.NewGuid().ToString("N"), modelId: opts.AdvertisedModelId, opts);
                await writer.WriteMessageStartAsync(ct).ConfigureAwait(false);

                var session = new AgentSession { InboundHop = hop };   // 수신 홉을 세션에 실어 ask_agent 가 +1 전파
                await foreach (var ev in orchestrator.RunAsync(prompt!, session, ct: ct).ConfigureAwait(false))
                {
                    // 상류(사내 서버) 인증 실패는 A2A 호출자에게 그대로 투영하되, 여기서도 집계한다 —
                    // 리스너 모드는 표준입력 루프를 돌지 않으므로 이 경로가 유일한 관측점이다.
                    switch (ev)
                    {
                        case AgentError e: authFailures.ObserveErrorCode("A2A", e.Code, e.Message); break;
                        case AgentDone:    authFailures.RecordSuccess(); break;
                    }

                    await writer.WriteAsync(ev, ct).ConfigureAwait(false);   // 투영 정책 §1-C
                }
            }
            finally { gate.Release(); }
        }
        catch (Exception ex)
        {
            AppLog.Warn("A2A", $"요청 처리 실패: {ex.Message}");
        }
        finally
        {
            try { resp.Close(); } catch { /* 이미 닫힘/전송됨 */ }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.StatusCode = status;
        resp.ContentType = "application/json";
        resp.ContentEncoding = Encoding.UTF8;
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
    }

    // 비-SSE 오류는 중첩 envelope({"error":{code,message}}) — AgentApiClient.ReadErrorAsync 정합.
    private static Task WriteErrorAsync(HttpListenerResponse resp, int status, string code, string message, CancellationToken ct)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { error = new { code, message } });
        return WriteJsonAsync(resp, status, body, ct);
    }
}
