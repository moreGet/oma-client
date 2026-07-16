using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// http_fetch 의 SSRF 경계. 이 도구의 목적은 사내망 접근이므로 사설 대역은 허용해야 하고,
/// 루프백·링크로컬(클라우드 메타데이터)만 막아야 한다 — 둘을 뒤섞으면 도구가 무의미해지거나 구멍이 남는다.
/// </summary>
public class UrlGuardTests
{
    private static async Task<UrlGuard.Result> Check(string url)
        => await UrlGuard.CheckAsync(new Uri(url), CancellationToken.None);

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]   // AWS/Azure/GCP 메타데이터
    [InlineData("http://169.254.170.2/v2/credentials")]        // ECS 자격증명
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://127.1.2.3/")]                          // 127/8 전체
    [InlineData("http://[::1]:9000/")]
    [InlineData("http://0.0.0.0:8080/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]                 // IPv4 매핑 IPv6 우회
    [InlineData("http://224.0.0.1/")]                          // 멀티캐스트
    public async Task Blocks_InternalAndMetadataAddresses(string url)
    {
        var result = await Check(url);

        Assert.False(result.Allowed);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    [InlineData("ftp://example.com/x")]
    [InlineData("gopher://example.com/")]
    public async Task Blocks_NonHttpSchemes(string url)
    {
        var result = await Check(url);

        Assert.False(result.Allowed);
        Assert.Contains("http", result.Reason!);
    }

    // 사설 대역은 이 도구의 의도된 사용처(사내망)다 — 막으면 안 된다.
    [Theory]
    [InlineData("http://10.0.0.5/api")]
    [InlineData("http://192.168.1.10:3000/")]
    [InlineData("http://172.16.5.4/")]
    public async Task Allows_PrivateIntranetAddresses(string url)
    {
        var result = await Check(url);

        Assert.True(result.Allowed, result.Reason);
    }

    [Fact]
    public async Task Blocks_UnresolvableHost()
    {
        var result = await Check("http://no-such-host.invalid/");

        Assert.False(result.Allowed);
    }
}
