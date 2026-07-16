using System;
using System.Threading.Tasks;
using System.Text.Json;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 이 클래스의 핵심 계약은 "정책이 없다(404)"와 "정책을 못 받았다(5xx/오프라인)"의 구분이다.
/// 둘을 뭉뚱그려 허용하면 /tools/policy 만 끊어 서버 정책 전체를 무력화할 수 있다.
/// </summary>
public class ToolPolicyServiceTests
{
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;

    private static ToolPolicy Cached(string[]? enabled = null, string[]? disabled = null)
        => new("cached", enabled, disabled);

    // ── fail-open: 서버가 정책 기능을 구현하지 않음(404) ──

    [Fact]
    public async Task NotImplemented_AllowsEverything()
    {
        var svc = new ToolPolicyService(new FakePolicyApiClient(() => ToolPolicyFetch.NotImplemented));
        await svc.LoadAsync();

        Assert.False(svc.IsLoaded);
        Assert.False(svc.IsUnavailable);
        Assert.True((await svc.EvaluateAsync("run_command", NoArgs)).Allowed);
        Assert.True(svc.IsExposed("run_command"));
    }

    // ── fail-closed: 정책을 못 받음 ──

    [Fact]
    public async Task FetchFailure_ThrowsAndBlocksEverything()
    {
        var api = new FakePolicyApiClient(() => ToolPolicyFetch.Failed);
        var svc = new ToolPolicyService(api);

        // 삼키면 안 되는 실패다 — 호출자가 사용자에게 알릴 수 있도록 던진다.
        await Assert.ThrowsAsync<AgentException>(() => svc.LoadAsync());

        Assert.True(svc.IsUnavailable);
        var decision = await svc.EvaluateAsync("read_file", NoArgs);
        Assert.False(decision.Allowed);
        Assert.False(svc.IsExposed("read_file"));   // 노출도 실행 게이트와 일관되게 막힌다
    }

    [Fact]
    public async Task FetchFailure_RetriesBeforeGivingUp()
    {
        var api = new FakePolicyApiClient(() => ToolPolicyFetch.Failed);
        var svc = new ToolPolicyService(api);

        await Assert.ThrowsAsync<AgentException>(() => svc.LoadAsync());

        // 일시적 블립으로 전 도구를 차단하지 않도록 재시도한다.
        Assert.Equal(3, api.PolicyCallCount);
    }

    [Fact]
    public async Task TransientFailure_ThenSuccess_Loads()
    {
        var attempt = 0;
        var api = new FakePolicyApiClient(() =>
            ++attempt < 2 ? ToolPolicyFetch.Failed : ToolPolicyFetch.Ok(Cached()));

        var svc = new ToolPolicyService(api);
        await svc.LoadAsync();

        Assert.True(svc.IsLoaded);
        Assert.True((await svc.EvaluateAsync("read_file", NoArgs)).Allowed);
    }

    [Fact]
    public async Task NotImplemented_IsNotRetried()
    {
        var api = new FakePolicyApiClient(() => ToolPolicyFetch.NotImplemented);
        await new ToolPolicyService(api).LoadAsync();

        Assert.Equal(1, api.PolicyCallCount);   // 404는 확정 답변이므로 재시도 낭비 금지
    }

    [Fact]
    public async Task BeforeLoad_BlocksEverything()
    {
        var svc = new ToolPolicyService(new FakePolicyApiClient(() => ToolPolicyFetch.NotImplemented));

        // LoadAsync 이전엔 정책을 모른다 → 차단이 기본값.
        Assert.True(svc.IsUnavailable);
        Assert.False((await svc.EvaluateAsync("run_command", NoArgs)).Allowed);
    }

    // ── cached 모드 규칙 ──

    [Fact]
    public async Task Cached_DisabledTakesPrecedenceOverEnabled()
    {
        var svc = new ToolPolicyService(new FakePolicyApiClient(
            () => ToolPolicyFetch.Ok(Cached(enabled: ["read_file"], disabled: ["read_file"]))));
        await svc.LoadAsync();

        Assert.False((await svc.EvaluateAsync("read_file", NoArgs)).Allowed);
        Assert.False(svc.IsExposed("read_file"));
    }

    [Fact]
    public async Task Cached_EnabledActsAsAllowlist()
    {
        var svc = new ToolPolicyService(new FakePolicyApiClient(
            () => ToolPolicyFetch.Ok(Cached(enabled: ["read_file"]))));
        await svc.LoadAsync();

        Assert.True((await svc.EvaluateAsync("read_file", NoArgs)).Allowed);
        Assert.False((await svc.EvaluateAsync("run_command", NoArgs)).Allowed);
    }

    [Fact]
    public async Task Cached_ToolNamesAreCaseInsensitive()
    {
        var svc = new ToolPolicyService(new FakePolicyApiClient(
            () => ToolPolicyFetch.Ok(Cached(disabled: ["RUN_COMMAND"]))));
        await svc.LoadAsync();

        Assert.False((await svc.EvaluateAsync("run_command", NoArgs)).Allowed);
    }

    [Fact]
    public async Task Cached_EmptyListsMeanNoRestriction()
    {
        var svc = new ToolPolicyService(new FakePolicyApiClient(
            () => ToolPolicyFetch.Ok(Cached(enabled: [], disabled: []))));
        await svc.LoadAsync();

        Assert.True((await svc.EvaluateAsync("anything", NoArgs)).Allowed);
    }

    [Fact]
    public async Task UnknownMode_DefaultsToCached()
    {
        var svc = new ToolPolicyService(new FakePolicyApiClient(
            () => ToolPolicyFetch.Ok(new ToolPolicy("typo-mode", null, ["run_command"]))));
        await svc.LoadAsync();

        Assert.Equal(ToolPolicyMode.Cached, svc.Mode);
        Assert.False((await svc.EvaluateAsync("run_command", NoArgs)).Allowed);
    }

    // ── realtime 모드 ──

    [Fact]
    public async Task Realtime_NoServerAnswer_FailsClosed()
    {
        var api = new FakePolicyApiClient(() => ToolPolicyFetch.Ok(new ToolPolicy("realtime", null, null)))
        {
            Authorization = null   // 인가 질의 실패
        };
        var svc = new ToolPolicyService(api);
        await svc.LoadAsync();

        Assert.Equal(ToolPolicyMode.Realtime, svc.Mode);
        Assert.False((await svc.EvaluateAsync("run_command", NoArgs)).Allowed);
    }

    [Fact]
    public async Task Realtime_ServerDecisionIsHonored()
    {
        var api = new FakePolicyApiClient(() => ToolPolicyFetch.Ok(new ToolPolicy("realtime", null, null)))
        {
            Authorization = new ToolAuthorization(false, "관리자 정책")
        };
        var svc = new ToolPolicyService(api);
        await svc.LoadAsync();

        var decision = await svc.EvaluateAsync("run_command", NoArgs);
        Assert.False(decision.Allowed);
        Assert.Equal("관리자 정책", decision.Reason);

        api.Authorization = new ToolAuthorization(true, null);
        Assert.True((await svc.EvaluateAsync("run_command", NoArgs)).Allowed);
    }

    [Fact]
    public async Task Realtime_ExposesAllTools()
    {
        var svc = new ToolPolicyService(new FakePolicyApiClient(
            () => ToolPolicyFetch.Ok(new ToolPolicy("realtime", null, ["run_command"]))));
        await svc.LoadAsync();

        // realtime 은 노출은 전체, 통제는 실행 직전 인가로 한다.
        Assert.True(svc.IsExposed("run_command"));
    }
}
