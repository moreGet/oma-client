using System;
using System.Collections.Generic;
using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class AgentRegistryOptionsTests
{
    private static Func<string, string?> Env(Dictionary<string, string?> map)
        => name => map.TryGetValue(name, out var v) ? v : null;

    private static (A2aOptions A2a, AgentRegistryOptions Reg) Build(Dictionary<string, string?> map)
    {
        var env = Env(map);
        var a2a = A2aOptions.FromEnvironment(env);
        var reg = AgentRegistryOptions.FromEnvironment(a2a, env);
        return (a2a, reg);
    }

    [Fact]
    public void Mode_defaults_to_broker_when_registry_on()
    {
        // LISTEN 설정 + REGISTRY 미명시 → 레지스트리 on → broker.
        var (a2a, reg) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://10.0.0.5:8080/",
            ["OHMYAGENT_ADVERTISE_URL"] = "http://10.0.0.5:8080",
        });

        Assert.Equal(A2aMode.Broker, a2a.Mode);
        Assert.True(reg.RegistryEnabled);
    }

    [Fact]
    public void Mode_defaults_to_token_when_registry_off()
    {
        var (a2a, reg) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://10.0.0.5:8080/",
            ["OHMYAGENT_REGISTRY"] = "off",
        });

        Assert.Equal(A2aMode.Token, a2a.Mode);
        Assert.False(reg.RegistryEnabled);
    }

    [Fact]
    public void Mode_explicit_overrides_default()
    {
        var (a2a, _) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://10.0.0.5:8080/",
            ["OHMYAGENT_A2A_MODE"] = "token",
        });
        Assert.Equal(A2aMode.Token, a2a.Mode);

        var (a2a2, _) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://10.0.0.5:8080/",
            ["OHMYAGENT_REGISTRY"] = "off",
            ["OHMYAGENT_A2A_MODE"] = "broker",
        });
        Assert.Equal(A2aMode.Broker, a2a2.Mode);
    }

    [Fact]
    public void Anon_flag_forces_anon_mode()
    {
        var (a2a, _) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://10.0.0.5:8080/",
            ["OHMYAGENT_A2A_ALLOW_ANON"] = "1",
        });
        Assert.Equal(A2aMode.Anon, a2a.Mode);
    }

    [Fact]
    public void Mode_anon_parses()
    {
        var (a2a, _) = Build(new()
        {
            ["OHMYAGENT_A2A_MODE"] = "anon",
        });
        Assert.Equal(A2aMode.Anon, a2a.Mode);
    }

    [Fact]
    public void Validate_rejects_wildcard_advertise_url()
    {
        var (_, reg) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
            // ADVERTISE_URL 미지정 → prefix(0.0.0.0)에서 유도 → 거부돼야 함.
        });

        Assert.True(reg.RegistryEnabled);
        Assert.Throws<InvalidOperationException>(reg.ValidateOrThrow);
    }

    [Fact]
    public void Validate_accepts_explicit_host()
    {
        var (_, reg) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
            ["OHMYAGENT_ADVERTISE_URL"] = "http://10.0.0.5:8080",
        });

        reg.ValidateOrThrow();   // 예외 없어야 함
        Assert.Equal("http://10.0.0.5:8080", reg.AdvertiseUrl);
    }

    [Fact]
    public void Validate_noop_when_registry_disabled()
    {
        var (_, reg) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://0.0.0.0:8080/",
            ["OHMYAGENT_REGISTRY"] = "off",
        });

        Assert.False(reg.RegistryEnabled);
        reg.ValidateOrThrow();   // 등록 비활성 → 검사 생략(예외 없음)
    }

    [Fact]
    public void Capabilities_parse_csv()
    {
        var (_, reg) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://10.0.0.5:8080/",
            ["OHMYAGENT_ADVERTISE_URL"] = "http://10.0.0.5:8080",
            ["OHMYAGENT_CAPABILITIES"] = " code-review , korean-nlp ,, ",
        });

        Assert.Equal(new[] { "code-review", "korean-nlp" }, reg.Capabilities);
    }

    [Fact]
    public void Agent_name_defaults_to_machine_name()
    {
        var (_, reg) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://10.0.0.5:8080/",
            ["OHMYAGENT_ADVERTISE_URL"] = "http://10.0.0.5:8080",
        });

        Assert.Equal(Environment.MachineName, reg.AgentName);
    }

    [Fact]
    public void Agent_name_explicit_wins()
    {
        var (_, reg) = Build(new()
        {
            ["OHMYAGENT_LISTEN"] = "http://10.0.0.5:8080/",
            ["OHMYAGENT_ADVERTISE_URL"] = "http://10.0.0.5:8080",
            ["OHMYAGENT_AGENT_NAME"] = "reviewer-1",
        });

        Assert.Equal("reviewer-1", reg.AgentName);
    }
}
