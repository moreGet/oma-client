using System;
using System.Text;
using System.Text.Json;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// JWT 에서 현재 사용자 식별자(member UUID = <c>sub</c> 클레임)를 추출하는 헬퍼.
/// 서버는 <c>/users/me</c> 에 id 를 내려주지 않으므로(검증됨), 채팅의 currentUserId/MyId 는
/// AuthToken(JWT) 의 payload <c>sub</c> 에서 얻는다. sender_id/member_id/user_id(direct) 전부 이 값이다.
/// 실패/형식오류 시 예외 없이 <see cref="string.Empty"/> 를 반환한다(호출자 graceful).
/// </summary>
public static class JwtIdentity
{
    /// <summary>JWT payload(2번째 세그먼트) 를 base64url 디코드해 <c>sub</c> 클레임을 반환. 실패 시 빈 문자열.</summary>
    public static string MemberId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        try
        {
            // "Bearer xxx" 형태로 들어와도 토큰만 취한다(방어적).
            var raw = token.Trim();
            var spaceIdx = raw.IndexOf(' ');
            if (spaceIdx >= 0)
                raw = raw[(spaceIdx + 1)..].Trim();

            var parts = raw.Split('.');
            if (parts.Length < 2)
                return string.Empty;

            var payloadJson = DecodeBase64Url(parts[1]);
            if (string.IsNullOrEmpty(payloadJson))
                return string.Empty;

            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("sub", out var sub) &&
                sub.ValueKind == JsonValueKind.String)
            {
                return sub.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // 형식 오류/디코드 실패는 무시하고 빈 문자열(예외 던지지 않음).
        }

        return string.Empty;
    }

    /// <summary>base64url(패딩 생략 가능) → UTF-8 문자열. 실패 시 빈 문자열.</summary>
    private static string DecodeBase64Url(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return string.Empty;

        // base64url → base64 치환 + 패딩 보정.
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return string.Empty;   // 잘못된 길이
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch
        {
            return string.Empty;
        }
    }
}
