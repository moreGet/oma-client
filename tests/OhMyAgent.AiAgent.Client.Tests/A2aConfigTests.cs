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
    public void Listen_without_token_fails_fast()
    {
        var opts = A2aOptions.FromEnvironment(Env(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
        }));

        Assert.True(opts.IsListenMode);
        Assert.Throws<InvalidOperationException>(opts.ValidateOrThrow);
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
