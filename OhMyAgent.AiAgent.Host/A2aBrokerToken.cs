using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OhMyAgent.AiAgent.Host;

/// <summary>브로커 토큰 검증 판정.</summary>
public enum BrokerVerdict
{
    Valid,
    BadFormat,
    BadAlg,
    BadSignature,
    AudMismatch,
    Expired,
    NotYetValid,
    BadIssuer,
}

/// <summary>
/// 서버 브로커가 발급한 ES256(ECDSA P-256) compact JWT 를 검증하는 순수 정적 메서드(스펙 §C-2, BCL only).
/// 외부 JWT 라이브러리 0 — System.Security.Cryptography.ECDsa 만 사용.
///
/// 시간 주입(now)으로 만료/미래 iat 를 시계 없이 테스트한다. 서명 검증 전에는 클레임을 신뢰하지 않는다
/// (헤더의 kid/alg 만 키 선택·alg 화이트리스트에 쓰고, aud/iss/exp/iat 는 서명 통과 후에 본다).
/// </summary>
public static class A2aBrokerToken
{
    private const string ExpectedAlg = "ES256";
    private const string ExpectedIssuer = "ohmyagent-server";
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);   // ±60s 시계 오차 허용

    /// <summary>
    /// 서명 검증 전에 헤더에서 kid/alg 만 읽는다(키 선택용, 미신뢰). 파싱 실패 → false.
    /// </summary>
    public static bool TryReadKid(string jwt, out string kid, out string alg)
    {
        kid = string.Empty;
        alg = string.Empty;

        if (string.IsNullOrWhiteSpace(jwt)) return false;
        var parts = jwt.Split('.');
        if (parts.Length != 3) return false;

        var headerBytes = DecodeBase64Url(parts[0]);
        if (headerBytes is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(headerBytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            kid = GetString(root, "kid");
            alg = GetString(root, "alg");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// compact JWT 를 검증한다. 순서: 형식 → alg 화이트리스트 → 서명 → 클레임(aud/iss/exp/iat).
    /// </summary>
    public static BrokerVerdict Validate(string jwt, string publicKeyPem, string ownAgentId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return BrokerVerdict.BadFormat;

        // 1) compact 3분할.
        var parts = jwt.Split('.');
        if (parts.Length != 3) return BrokerVerdict.BadFormat;

        var headerBytes = DecodeBase64Url(parts[0]);
        var payloadBytes = DecodeBase64Url(parts[1]);
        var signature = DecodeBase64Url(parts[2]);
        if (headerBytes is null || payloadBytes is null || signature is null)
            return BrokerVerdict.BadFormat;

        // 2) alg 화이트리스트 — 정확히 ES256 만. none/HS256/기타 즉시 거부(alg 혼동 공격 봉쇄).
        string alg;
        try
        {
            using var headerDoc = JsonDocument.Parse(headerBytes);
            if (headerDoc.RootElement.ValueKind != JsonValueKind.Object) return BrokerVerdict.BadFormat;
            alg = GetString(headerDoc.RootElement, "alg");
        }
        catch (JsonException)
        {
            return BrokerVerdict.BadFormat;
        }
        if (!string.Equals(alg, ExpectedAlg, StringComparison.Ordinal))
            return BrokerVerdict.BadAlg;

        // 3) 서명 검증 — ES256 서명은 r‖s 64바이트 raw(P1363)이므로 DSASignatureFormat.IeeeP1363.
        //    "header.payload" 의 ASCII 바이트에 대한 서명.
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);

            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var ok = ecdsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!ok) return BrokerVerdict.BadSignature;
        }
        catch (Exception)
        {
            // 키 임포트 실패/형식 오류 등 → 서명 신뢰 불가.
            return BrokerVerdict.BadSignature;
        }

        // 4) 클레임 — 서명 통과 후에만 신뢰.
        JsonElement claims;
        JsonDocument payloadDoc;
        try
        {
            payloadDoc = JsonDocument.Parse(payloadBytes);
        }
        catch (JsonException)
        {
            return BrokerVerdict.BadFormat;
        }

        using (payloadDoc)
        {
            claims = payloadDoc.RootElement;
            if (claims.ValueKind != JsonValueKind.Object) return BrokerVerdict.BadFormat;

            var aud = GetString(claims, "aud");
            if (!string.Equals(aud, ownAgentId, StringComparison.Ordinal))
                return BrokerVerdict.AudMismatch;

            var iss = GetString(claims, "iss");
            if (!string.Equals(iss, ExpectedIssuer, StringComparison.Ordinal))
                return BrokerVerdict.BadIssuer;

            // exp 필수 — 없으면 fail-closed(만료 취급).
            if (!TryGetUnixSeconds(claims, "exp", out var exp))
                return BrokerVerdict.Expired;
            var expAt = DateTimeOffset.FromUnixTimeSeconds(exp);
            if (now > expAt + Skew)
                return BrokerVerdict.Expired;

            // iat — 있으면 미래 발급 거부. 없으면 검사 생략(서버는 항상 세팅).
            if (TryGetUnixSeconds(claims, "iat", out var iat))
            {
                var iatAt = DateTimeOffset.FromUnixTimeSeconds(iat);
                if (iatAt > now + Skew)
                    return BrokerVerdict.NotYetValid;
            }

            return BrokerVerdict.Valid;
        }
    }

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryGetUnixSeconds(JsonElement el, string prop, out long seconds)
    {
        seconds = 0;
        if (!el.TryGetProperty(prop, out var p) || p.ValueKind != JsonValueKind.Number)
            return false;
        if (p.TryGetInt64(out seconds)) return true;
        if (p.TryGetDouble(out var d)) { seconds = (long)d; return true; }
        return false;
    }

    /// <summary>base64url(패딩 없음) 디코드 — BCL only. '-'/'_'→'+'/'/' 치환 후 패딩 복원.</summary>
    private static byte[]? DecodeBase64Url(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;

        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 2: t += "=="; break;
            case 3: t += "="; break;
            case 0: break;
            default: return null;   // 유효한 base64url 길이가 아님.
        }

        try
        {
            return Convert.FromBase64String(t);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
