using System;
using System.Security.Cryptography;
using System.Text;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// A2A Bearer 토큰 인증 — 순수 판정(스펙 §1-E). /agent/chat 은 토큰 필수,
/// /health 는 호출부에서 우회(Public). 비교는 타이밍 세이프.
/// </summary>
public static class A2aAuth
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Authorization 헤더가 설정 토큰과 일치하는지 판정.
    ///   AllowAnonymous → 항상 true.
    ///   토큰 미설정 → 항상 false(리스너 기동 자체는 ValidateOrThrow 가 막지만, 이 함수 단독도 안전 기본).
    ///   "Bearer " 접두 부재/헤더 null → false.
    /// </summary>
    public static bool IsAuthorized(string? authHeader, A2aOptions opts)
    {
        if (opts.AllowAnonymous) return true;
        if (string.IsNullOrEmpty(opts.Token)) return false;
        if (authHeader is null || !authHeader.StartsWith(BearerPrefix, StringComparison.Ordinal))
            return false;

        var presented = authHeader.AsSpan(BearerPrefix.Length).Trim();
        return FixedTimeEquals(presented, opts.Token);
    }

    private static bool FixedTimeEquals(ReadOnlySpan<char> presented, string expected)
    {
        var a = Encoding.UTF8.GetBytes(presented.ToString());
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
