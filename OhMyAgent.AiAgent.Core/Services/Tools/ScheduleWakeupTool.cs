using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Loop;
using OhMyAgent.AiAgent.Client.Services.Loop;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 자율 페이싱 루프에서 모델이 "다음 실행 시점"을 스스로 정하는 메타 도구.
/// 시스템 부작용이 없어 위험도 ReadOnly(승인 카드 없이 실행).
///
/// 루프 컨트롤러를 직접 모르고 <see cref="IWakeupSink"/> 만 안다 — 도구가 엔진을 알면 순환 의존이 생기고,
/// 루프 밖에서(또는 서브에이전트가) 호출했을 때 부작용을 막을 방법이 사라진다.
/// </summary>
public sealed class ScheduleWakeupTool(IWakeupSink wakeups) : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"delaySeconds":{"type":"number","description":"Seconds to wait before the next run (clamped to 5s..3600s)."},"reason":{"type":"string","description":"Short reason for this pacing decision (shown to the user)."},"done":{"type":"boolean","description":"true = the recurring task is complete; stop the loop."}},"required":["delaySeconds"]}
        """);

    public string Name => "schedule_wakeup";

    public string Description =>
        "Schedule when this recurring task should run next. Only meaningful while a /loop in autonomous pacing mode is active — " +
        "call it exactly once, at the very end of your turn, after you have finished the work. " +
        "Pass 'delaySeconds' for how long to wait before the next run and a short 'reason'. " +
        "Set 'done': true when the recurring task is finished and the loop should stop.";

    public JsonElement ParametersSchema => Schema;

    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (GetDouble(args, "delaySeconds") is not { } requested)
            return Task.FromResult(ToolResult.Fail("'delaySeconds' 숫자 값이 필요합니다."));

        var reason = ToolSchemas.GetString(args, "reason");
        var done = ToolSchemas.GetBool(args, "done");

        if (!wakeups.IsArmed)
            return Task.FromResult(ToolResult.Fail("현재 /loop 실행 중이 아니라 예약을 무시했습니다."));

        // A4 — 사용자가 간격을 명시한 루프에서는 모델의 페이싱 판단을 받지 않는다.
        // 실패로 돌려주면 모델이 재시도로 턴을 낭비하므로 성공 + 무시 안내로 끝낸다.
        if (wakeups.ArmedMode == LoopMode.FixedInterval)
            return Task.FromResult(ToolResult.Ok("고정 간격 루프라 예약을 무시했습니다(간격이 우선)."));

        if (done)
        {
            return Task.FromResult(Write(new WakeupRequest(TimeSpan.Zero, reason, true, DateTimeOffset.UtcNow),
                "반복 작업 완료로 표시했습니다. 이번 턴 후 루프를 종료합니다."));
        }

        var clamped = LoopPolicy.ClampAutonomousDelay(requested);
        // 클램프됐다면 실제 예약값을 알려준다 — 모델이 자기가 요청한 값대로 됐다고 착각하면 다음 판단이 어긋난다.
        var note = WasClamped(requested, clamped)
            ? $" (요청 {DescribeRequested(requested)} → {LoopIntervalParser.Format(clamped)}로 조정)"
            : "";

        return Task.FromResult(Write(new WakeupRequest(clamped, reason, false, DateTimeOffset.UtcNow),
            $"다음 실행을 {LoopIntervalParser.Format(clamped)} 뒤로 예약했습니다.{note}"));
    }

    private ToolResult Write(WakeupRequest request, string okText)
        => wakeups.TryWrite(request, out var reject)
            ? ToolResult.Ok(okText)
            : ToolResult.Fail(reject ?? "예약을 저장하지 못했습니다.");

    private static bool WasClamped(double requested, TimeSpan clamped)
        => !double.IsFinite(requested) || Math.Abs(clamped.TotalSeconds - requested) > 0.001;

    private static string DescribeRequested(double seconds)
        => double.IsFinite(seconds) && seconds > 0
            ? LoopIntervalParser.Format(TimeSpan.FromSeconds(seconds))
            : "잘못된 값";

    private static double? GetDouble(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.Number
            && p.TryGetDouble(out var v))
            return v;
        return null;
    }
}
