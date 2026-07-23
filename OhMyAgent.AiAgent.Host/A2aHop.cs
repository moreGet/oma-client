using System;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// X-A2A-Hop 홉 카운트 파싱·판정(스펙 §2-F). A→B→A 순환·무한 위임을 유한 깊이에서 봉쇄.
/// </summary>
public static class A2aHop
{
    public const string HeaderName = "X-A2A-Hop";

    /// <summary>헤더 값을 홉 수로 해석. null/비정수/음수 → 0.</summary>
    public static int Read(string? headerValue)
        => int.TryParse(headerValue, out var hop) && hop > 0 ? hop : 0;

    /// <summary>수신 홉이 한계 이상이면 거절(508) 대상.</summary>
    public static bool ExceedsLimit(int hop, int maxHops) => hop >= maxHops;
}
