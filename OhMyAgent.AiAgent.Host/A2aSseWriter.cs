using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// orchestrator AgentEvent → SSE 와이어 프레임 매퍼(AgentApiClient.Dispatch 의 역방향, 투영 정책 §1-C).
/// 임의 Stream 을 대상으로 쓰므로 HttpListener 없이 단위 테스트 가능하다.
///
/// A2A 투영 정책(§0-2 이중 실행 방지):
///   - content_delta / message_stop / error 만 기본 방출.
///   - tool_call / iteration / assistant_complete / thinking_complete / notice 는 억제(내부 실행).
///   - message_stop.stop_reason 은 항상 "end_turn"(비-tool_use) → 호출자 orchestrator 루프 정상 종료.
///   - thinking_delta 는 ExposeThinking 옵트인 시에만.
/// </summary>
public sealed class A2aSseWriter(Stream output, string runId, string modelId, A2aOptions opts)
{
    // 명시적 옵션: 네이밍 정책 없음(익명 프로퍼티명 그대로) + 비-ASCII/제어문자만 이스케이프.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>SSE 응답 헤더 셋업(스펙 §1-B). 프레임마다 flush 하므로 SendChunked.</summary>
    public static void PrepareResponse(HttpListenerResponse resp)
    {
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream";
        resp.ContentEncoding = Encoding.UTF8;
        resp.SendChunked = true;
        resp.KeepAlive = true;
        resp.Headers["Cache-Control"] = "no-cache";
    }

    public Task WriteMessageStartAsync(CancellationToken ct)
        => FrameAsync("message_start", Json(new { id = runId, model = modelId }), ct);

    /// <summary>투영 정책 적용 후 프레임 방출. 억제 대상 이벤트는 0 바이트(no-op).</summary>
    public async Task WriteAsync(AgentEvent ev, CancellationToken ct)
    {
        switch (ev)
        {
            case AgentTextDelta d:
                await FrameAsync("content_delta", Json(new { delta = d.Text }), ct).ConfigureAwait(false);
                break;

            case AgentThinkingDelta t when opts.ExposeThinking:
                await FrameAsync("thinking_delta", Json(new { delta = t.Text }), ct).ConfigureAwait(false);
                break;

            case AgentDone done:
                await FrameAsync("message_stop", BuildStop(done.LastUsage), ct).ConfigureAwait(false);
                break;

            case AgentError e:
                await FrameAsync("error",
                    Json(new { error = new { code = e.Code, message = e.Message } }), ct).ConfigureAwait(false);
                break;

            // 억제(§1-C): AgentThinkingDelta(off)/AgentToolCallStarted/AgentAwaitingApproval/
            // AgentToolCallResult/AgentIterationAdvanced/AgentAssistantMessageComplete/
            // AgentThinkingComplete/AgentNotice → 방출 없음.
        }
    }

    // stop_reason 은 항상 end_turn(§0-2). Usage 는 서버 필드명(prompt/completion/total_tokens) — 레코드 속성명 그대로.
    private static string BuildStop(Usage? usage)
        => Json(new
        {
            stop_reason = "end_turn",
            usage = usage ?? new Usage(0, 0),
            thinking = "",
            thinking_signature = "",
        });

    private async Task FrameAsync(string ev, string dataJson, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {dataJson}\n\n");
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);   // 프레임마다 flush = 진짜 스트리밍
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);
}
