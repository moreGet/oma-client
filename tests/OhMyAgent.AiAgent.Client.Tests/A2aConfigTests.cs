using System;
using System.Collections.Generic;
using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class A2aConfigTests
{
    private static Func<string, string?> Env(Dictionary<string, string?> map)
        => name => map.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void Defaults_apply_when_env_absent()
    {
        var opts = A2aOptions.FromEnvironment(Env(new()));
        Assert.False(opts.IsListenMode);
        Assert.Equal(4, opts.MaxConcurrency);
        Assert.Equal(3, opts.MaxHops);
        Assert.False(opts.AllowAnonymous);
        Assert.False(opts.ExposeThinking);
        Assert.Null(opts.Token);
    }

    [Fact]
    public void Token_mode_without_token_fails_fast()
    {
        // token 모드를 명시(또는 레지스트리 off)했는데 공유 토큰이 없으면 fail-fast.
        var opts = A2aOptions.FromEnvironment(Env(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
            ["OHMYAGENT_A2A_MODE"] = "token",
        }));

        Assert.Equal(A2aMode.Token, opts.Mode);
        Assert.True(opts.IsListenMode);
        Assert.Throws<InvalidOperationException>(opts.ValidateOrThrow);
    }

    [Fact]
    public void Registry_off_defaults_to_token_and_without_token_fails_fast()
    {
        // 레지스트리 off → 기본 모드 token → 토큰 없으면 fail-fast(기존 무회귀).
        var opts = A2aOptions.FromEnvironment(Env(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
            ["OHMYAGENT_REGISTRY"] = "off",
        }));

        Assert.Equal(A2aMode.Token, opts.Mode);
        Assert.Throws<InvalidOperationException>(opts.ValidateOrThrow);
    }

    [Fact]
    public void Broker_mode_allows_listen_without_shared_token()
    {
        // LISTEN 만 설정(레지스트리 on 기본) → 모드 Broker → 서버 발급 ES256 토큰을 쓰므로
        // 공유 토큰 없이도 기동 통과. (실기 통합에서 잡힌 회귀 — Host 가 broker 모드인데 fail-fast 하던 버그.)
        var opts = A2aOptions.FromEnvironment(Env(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
        }));

        Assert.Equal(A2aMode.Broker, opts.Mode);
        Assert.True(opts.IsListenMode);
        Assert.Null(opts.Token);
        opts.ValidateOrThrow();   // 예외 없어야 함
    }

    [Fact]
    public void Listen_with_token_validates()
    {
        var opts = A2aOptions.FromEnvironment(Env(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
            ["OHMYAGENT_A2A_TOKEN"] = "t",
        }));

        opts.ValidateOrThrow();   // 예외 없어야 함
        Assert.Equal("t", opts.Token);
    }

    [Fact]
    public void Anonymous_optout_allows_listen_without_token()
    {
        var opts = A2aOptions.FromEnvironment(Env(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
            ["OHMYAGENT_A2A_ALLOW_ANON"] = "1",
        }));

        opts.ValidateOrThrow();   // anon 옵트인 → 통과
        Assert.True(opts.AllowAnonymous);
    }

    [Fact]
    public void Prefix_is_normalized_with_trailing_slash()
    {
        var opts = A2aOptions.FromEnvironment(Env(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080",   // 끝 '/' 없음
        }));

        Assert.EndsWith("/", opts.Prefix);
        Assert.Equal("http://0.0.0.0:8080/", opts.Prefix);
    }

    [Fact]
    public void Custom_limits_and_flags_parse()
    {
        var opts = A2aOptions.FromEnvironment(Env(new()
        {
            ["OHMYAGENT_A2A_MAX_CONCURRENCY"] = "8",
            ["OHMYAGENT_A2A_MAX_HOPS"] = "5",
            ["OHMYAGENT_A2A_EXPOSE_THINKING"] = "1",
        }));

        Assert.Equal(8, opts.MaxConcurrency);
        Assert.Equal(5, opts.MaxHops);
        Assert.True(opts.ExposeThinking);
    }

    [Fact]
    public void Model_id_falls_back_to_default_when_env_absent()
    {
        var opts = A2aOptions.FromEnvironment(Env(new()), defaultModelId: "settings-model");
        Assert.Equal("settings-model", opts.AdvertisedModelId);
    }

    [Fact]
    public void Model_id_env_overrides_default()
    {
        var opts = A2aOptions.FromEnvironment(
            Env(new() { ["OHMYAGENT_A2A_MODEL_ID"] = "explicit" }),
            defaultModelId: "settings-model");
        Assert.Equal("explicit", opts.AdvertisedModelId);
    }
}
