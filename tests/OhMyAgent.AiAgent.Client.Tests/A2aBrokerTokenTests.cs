using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// A2aBrokerToken.Validate 집중 검증 — 자체 생성 P-256 키쌍으로 유효/만료/미래iat/aud불일치/서명훼손/
/// alg=none/alg=HS256/미지 issuer/형식 케이스를 서버 없이 잠근다. now 주입으로 시계 없이 만료 검증.
/// </summary>
public class A2aBrokerTokenTests
{
    // 고정 기준 시각 — 만료/미래 iat 를 결정적으로 검증.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private const string OwnAgentId = "me-agent";
    private const string Issuer = "ohmyagent-server";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Segment(object value) =>
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(value));

    // header/payload → ES256 서명 compact JWT.
    private static string Sign(ECDsa key, object header, object payload)
    {
        var h = Segment(header);
        var p = Segment(payload);
        var signingInput = Encoding.ASCII.GetBytes(h + "." + p);
        var sig = key.SignData(signingInput, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{h}.{p}.{Base64Url(sig)}";
    }

    private static object DefaultHeader => new { alg = "ES256", kid = "k1", typ = "JWT" };

    private static object Claims(long exp, long iat, string aud = OwnAgentId, string iss = Issuer) =>
        new { iss, sub = "member-1", cid = "caller-1", aud, iat, exp, jti = "j-1" };

    private static long Unix(DateTimeOffset t) => t.ToUnixTimeSeconds();

    [Fact]
    public void Valid_token_passes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var jwt = Sign(key, DefaultHeader, Claims(Unix(Now.AddSeconds(120)), Unix(Now)));

        Assert.Equal(BrokerVerdict.Valid, A2aBrokerToken.Validate(jwt, pem, OwnAgentId, Now));
    }

    [Fact]
    public void Expired_token_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        // exp 가 now-120s(skew 60s 초과 과거).
        var jwt = Sign(key, DefaultHeader, Claims(Unix(Now.AddSeconds(-120)), Unix(Now.AddSeconds(-240))));

        Assert.Equal(BrokerVerdict.Expired, A2aBrokerToken.Validate(jwt, pem, OwnAgentId, Now));
    }

    [Fact]
    public void Future_iat_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        // iat 가 now+120s(skew 초과 미래), exp 는 더 미래라 유효.
        var jwt = Sign(key, DefaultHeader, Claims(Unix(Now.AddSeconds(300)), Unix(Now.AddSeconds(120))));

        Assert.Equal(BrokerVerdict.NotYetValid, A2aBrokerToken.Validate(jwt, pem, OwnAgentId, Now));
    }

    [Fact]
    public void Audience_mismatch_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var jwt = Sign(key, DefaultHeader, Claims(Unix(Now.AddSeconds(120)), Unix(Now), aud: "someone-else"));

        Assert.Equal(BrokerVerdict.AudMismatch, A2aBrokerToken.Validate(jwt, pem, OwnAgentId, Now));
    }

    [Fact]
    public void Bad_issuer_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var jwt = Sign(key, DefaultHeader, Claims(Unix(Now.AddSeconds(120)), Unix(Now), iss: "evil-issuer"));

        Assert.Equal(BrokerVerdict.BadIssuer, A2aBrokerToken.Validate(jwt, pem, OwnAgentId, Now));
    }

    [Fact]
    public void Tampered_signature_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // 다른 키의 공개키로 검증 → 서명 불일치.
        var wrongPem = otherKey.ExportSubjectPublicKeyInfoPem();
        var jwt = Sign(key, DefaultHeader, Claims(Unix(Now.AddSeconds(120)), Unix(Now)));

        Assert.Equal(BrokerVerdict.BadSignature, A2aBrokerToken.Validate(jwt, wrongPem, OwnAgentId, Now));
    }

    [Fact]
    public void Alg_none_rejected_before_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        // alg=none — 서명 검증 전 alg 화이트리스트에서 거부돼야 한다(alg 혼동 공격).
        var jwt = Sign(key, new { alg = "none", kid = "k1" }, Claims(Unix(Now.AddSeconds(120)), Unix(Now)));

        Assert.Equal(BrokerVerdict.BadAlg, A2aBrokerToken.Validate(jwt, pem, OwnAgentId, Now));
    }

    [Fact]
    public void Alg_hs256_forgery_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var jwt = Sign(key, new { alg = "HS256", kid = "k1" }, Claims(Unix(Now.AddSeconds(120)), Unix(Now)));

        Assert.Equal(BrokerVerdict.BadAlg, A2aBrokerToken.Validate(jwt, pem, OwnAgentId, Now));
    }

    [Fact]
    public void Malformed_token_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        Assert.Equal(BrokerVerdict.BadFormat, A2aBrokerToken.Validate("not-a-jwt", pem, OwnAgentId, Now));
        Assert.Equal(BrokerVerdict.BadFormat, A2aBrokerToken.Validate("a.b.c.d", pem, OwnAgentId, Now));
        Assert.Equal(BrokerVerdict.BadFormat, A2aBrokerToken.Validate("", pem, OwnAgentId, Now));
    }

    [Fact]
    public void TryReadKid_reads_header_without_verification()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jwt = Sign(key, DefaultHeader, Claims(Unix(Now.AddSeconds(120)), Unix(Now)));

        Assert.True(A2aBrokerToken.TryReadKid(jwt, out var kid, out var alg));
        Assert.Equal("k1", kid);
        Assert.Equal("ES256", alg);
    }

    [Fact]
    public void TryReadKid_fails_on_garbage()
    {
        Assert.False(A2aBrokerToken.TryReadKid("garbage", out _, out _));
    }
}
