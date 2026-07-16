using System;
using System.Text;
using OhMyAgent.AiAgent.Client.Services.Chat;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// MemberId 의 반환값은 채팅의 currentUserId 가 되고, 그 값이 모든 말풍선의 IsMine(좌/우 정렬)을 정한다.
/// 조용히 빈 문자열을 반환하는 설계라 오동작이 예외 없이 UI 로 새어나간다 — 그래서 테스트 가치가 높다.
/// </summary>
public class JwtIdentityTests
{
    /// <summary>서명은 검증하지 않으므로(payload 만 읽는다) 테스트용 토큰은 임의 서명으로 충분하다.</summary>
    private static string MakeToken(string payloadJson, string header = """{"alg":"HS256","typ":"JWT"}""")
        => $"{B64Url(header)}.{B64Url(payloadJson)}.fake-signature";

    private static string B64Url(string s)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void MemberId_ExtractsSubClaim()
    {
        var token = MakeToken("""{"sub":"11111111-2222-3333-4444-555555555555","exp":9999999999}""");

        Assert.Equal("11111111-2222-3333-4444-555555555555", JwtIdentity.MemberId(token));
    }

    [Fact]
    public void MemberId_StripsBearerPrefix()
    {
        var token = "Bearer " + MakeToken("""{"sub":"abc"}""");

        Assert.Equal("abc", JwtIdentity.MemberId(token));
    }

    // base64url 은 패딩('=')을 생략한다. 길이 % 4 가 2/3 인 경우 각각 "=="/"=" 를 복원해야 하며,
    // 이 보정이 틀리면 특정 길이의 payload 에서만 조용히 실패한다.
    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("abcde")]
    [InlineData("abcdef")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void MemberId_HandlesAllBase64UrlPaddingLengths(string sub)
    {
        var token = MakeToken($$"""{"sub":"{{sub}}"}""");

        Assert.Equal(sub, JwtIdentity.MemberId(token));
    }

    [Fact]
    public void MemberId_HandlesBase64UrlSpecificChars()
    {
        // '+' 와 '/' 를 만들어내는 payload — base64url 에선 '-' 와 '_' 로 치환된다.
        var sub = "??>>??>>~~~ÿþ";
        var token = MakeToken($$"""{"sub":"{{sub}}"}""");

        Assert.Equal(sub, JwtIdentity.MemberId(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]                      // payload 가 유효 base64 가 아님
    [InlineData("aaa.!!!not-base64!!!.ccc")]
    public void MemberId_ReturnsEmptyOnMalformedInput(string? token)
    {
        Assert.Equal(string.Empty, JwtIdentity.MemberId(token));
    }

    [Fact]
    public void MemberId_ReturnsEmptyWhenSubMissing()
    {
        Assert.Equal(string.Empty, JwtIdentity.MemberId(MakeToken("""{"name":"kim"}""")));
    }

    [Fact]
    public void MemberId_ReturnsEmptyWhenSubIsNotString()
    {
        // sub 가 숫자면 GetString() 이 던진다 — 삼키고 빈 문자열이어야 한다.
        Assert.Equal(string.Empty, JwtIdentity.MemberId(MakeToken("""{"sub":12345}""")));
    }

    [Fact]
    public void MemberId_ReturnsEmptyWhenPayloadIsNotObject()
    {
        Assert.Equal(string.Empty, JwtIdentity.MemberId(MakeToken("[1,2,3]")));
    }
}
