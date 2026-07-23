using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class A2aAuthTests
{
    private static A2aOptions WithToken(string? token, bool anon = false)
        => new() { Token = token, AllowAnonymous = anon };

    [Fact]
    public void Matching_token_authorizes()
        => Assert.True(A2aAuth.IsAuthorized("Bearer secret-123", WithToken("secret-123")));

    [Fact]
    public void Mismatched_token_rejected()
        => Assert.False(A2aAuth.IsAuthorized("Bearer wrong", WithToken("secret-123")));

    [Fact]
    public void Missing_bearer_prefix_rejected()
        => Assert.False(A2aAuth.IsAuthorized("secret-123", WithToken("secret-123")));

    [Fact]
    public void Null_header_rejected()
        => Assert.False(A2aAuth.IsAuthorized(null, WithToken("secret-123")));

    [Fact]
    public void Unset_token_rejects_everything()
        => Assert.False(A2aAuth.IsAuthorized("Bearer anything", WithToken(null)));

    [Fact]
    public void Allow_anonymous_authorizes_without_token()
        => Assert.True(A2aAuth.IsAuthorized(null, WithToken(null, anon: true)));

    [Fact]
    public void Bearer_value_is_trimmed_before_compare()
        => Assert.True(A2aAuth.IsAuthorized("Bearer   secret-123  ", WithToken("secret-123")));
}
