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
/// 서버 도구 정책. GET /api/v1/tools/policy. 미구현/오프라인(404/오류)은 graceful null 처리.
/// 직렬화: AgentJson.Options(snake_case).
/// </summary>
public sealed record ToolPolicy(
    [property: JsonPropertyName("mode")]     string Mode,                      // "cached" | "realtime"
    [property: JsonPropertyName("enabled")]  IReadOnlyList<string>? Enabled,   // null/없음 = 전체 허용
    [property: JsonPropertyName("disabled")] IReadOnlyList<string>? Disabled); // disabled가 enabled보다 우선

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
