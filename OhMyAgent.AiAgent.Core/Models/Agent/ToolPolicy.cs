using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>서버 도구 정책 모드. 로그인 시 1회 수신해 세션 캐시한다.</summary>
public enum ToolPolicyMode
{
    /// <summary>로그인 때 받은 enabled/disabled 목록을 로컬 적용(서버 왕복 없음).</summary>
    Cached,

    /// <summary>도구 실행 직전마다 서버에 인가 질의(즉시 반영).</summary>
    Realtime
}

/// <summary>
/// 서버 도구 정책. GET /api/v1/tools/policy.
/// 직렬화: AgentJson.Options(snake_case).
/// </summary>
public sealed record ToolPolicy(
    [property: JsonPropertyName("mode")]     string Mode,                      // "cached" | "realtime"
    [property: JsonPropertyName("enabled")]  IReadOnlyList<string>? Enabled,   // null/없음 = 전체 허용
    [property: JsonPropertyName("disabled")] IReadOnlyList<string>? Disabled); // disabled가 enabled보다 우선

/// <summary>
/// 도구 정책 조회 결과. "정책이 없다"와 "정책을 못 받았다"를 반드시 구분해야 한다 —
/// 둘을 뭉뚱그리면 /tools/policy 만 끊거나 500을 내는 것으로 서버 정책 전체를 무력화할 수 있다.
/// </summary>
public enum ToolPolicyFetchStatus
{
    /// <summary>정책 수신 성공.</summary>
    Ok,

    /// <summary>404 — 서버가 정책 기능을 구현하지 않음. 정책 부재가 확정이므로 fail-open 해도 안전.</summary>
    NotImplemented,

    /// <summary>401/5xx/타임아웃/오프라인/파싱 실패 — 정책이 있는데 못 받았을 수 있으므로 fail-closed.</summary>
    Failed
}

/// <summary>정책 조회 결과 + 본문. <see cref="ToolPolicyFetchStatus.Ok"/> 일 때만 <see cref="Policy"/> 가 non-null.</summary>
public sealed record ToolPolicyFetch(ToolPolicyFetchStatus Status, ToolPolicy? Policy)
{
    public static ToolPolicyFetch Ok(ToolPolicy policy) => new(ToolPolicyFetchStatus.Ok, policy);
    public static ToolPolicyFetch NotImplemented { get; } = new(ToolPolicyFetchStatus.NotImplemented, null);
    public static ToolPolicyFetch Failed { get; } = new(ToolPolicyFetchStatus.Failed, null);
}

/// <summary>실시간 인가 응답. POST /api/v1/tools/authorize.</summary>
public sealed record ToolAuthorization(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("reason")]  string? Reason);

/// <summary>정책 게이트 평가 결과(클라 내부 전용 — 와이어에 직렬화하지 않음).</summary>
public sealed record ToolGateDecision(bool Allowed, string? Reason)
{
    public static ToolGateDecision Allow() => new(true, null);
    public static ToolGateDecision Deny(string reason) => new(false, reason);
}
