using System;
using System.Collections.Generic;
using System.Linq;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// 레지스트리 등록 설정(스펙 §D). A2A 전송 설정(<see cref="A2aOptions"/>)과 관심사를 분리한다
/// (전송=수신 인증/홉, 등록=이름/광고URL/capabilities). env 단일 소스에서 조립.
///
/// env → 필드:
///   OHMYAGENT_AGENT_NAME    → AgentName     (기본 Environment.MachineName)
///   OHMYAGENT_ADVERTISE_URL → AdvertiseUrl  (미설정 시 LISTEN prefix 에서 유도. 0.0.0.0/+/* 는 광고 불가 → 필수)
///   OHMYAGENT_CAPABILITIES  → Capabilities  (csv, 공백 trim·빈 항목 제거)
///   OHMYAGENT_REGISTRY      → RegistryEnabled(LISTEN 시 on 기본, off/0/false → false)
/// </summary>
public sealed record AgentRegistryOptions
{
    public bool RegistryEnabled { get; init; }

    public string AgentName { get; init; } = string.Empty;

    /// <summary>다른 에이전트가 접속할 내 공개 base URL(예 http://10.0.0.5:8080). 0.0.0.0 광고 불가.</summary>
    public string AdvertiseUrl { get; init; } = string.Empty;

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public static AgentRegistryOptions FromEnvironment(A2aOptions a2a, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        string Get(string name) => getEnv(name)?.Trim() ?? string.Empty;

        var name = Get("OHMYAGENT_AGENT_NAME");
        if (string.IsNullOrEmpty(name)) name = Environment.MachineName;

        var caps = Get("OHMYAGENT_CAPABILITIES")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        // AdvertiseUrl: 명시값 우선, 없으면 LISTEN prefix 에서 유도(끝 '/' 제거). 유도값이 0.0.0.0 등
        // 와일드카드면 ValidateOrThrow 가 거부한다(그때만 OHMYAGENT_ADVERTISE_URL 이 필수가 된다).
        var advertise = Get("OHMYAGENT_ADVERTISE_URL");
        if (string.IsNullOrEmpty(advertise))
            advertise = DeriveFromPrefix(a2a.Prefix);

        return new AgentRegistryOptions
        {
            RegistryEnabled = A2aOptions.IsRegistryEnabled(getEnv, a2a.Prefix),
            AgentName = name,
            AdvertiseUrl = advertise,
            Capabilities = caps,
        };
    }

    private static string DeriveFromPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;
        return prefix.TrimEnd('/');
    }

    /// <summary>
    /// 등록 사용 시 광고 URL 검증. host 가 0.0.0.0/::/+/*/빈값이면 거부한다 — 그런 바인딩 주소는 다른
    /// 에이전트가 실제로 접속할 수 없어 등록해도 발견이 무의미하기 때문이다(§D).
    /// </summary>
    public void ValidateOrThrow()
    {
        if (!RegistryEnabled) return;

        if (string.IsNullOrWhiteSpace(AdvertiseUrl))
            throw new InvalidOperationException(
                "레지스트리 등록에는 OHMYAGENT_ADVERTISE_URL 이 필요합니다 — 다른 에이전트가 접속 가능한 host:port 를 지정하세요.");

        var host = Uri.TryCreate(AdvertiseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

        var wildcard =
            host is null or "0.0.0.0" or "::" or "[::]" or "+" or "*"
            || AdvertiseUrl.Contains("://+", StringComparison.Ordinal)
            || AdvertiseUrl.Contains("://*", StringComparison.Ordinal)
            || AdvertiseUrl.Contains("0.0.0.0", StringComparison.Ordinal)
            || AdvertiseUrl.Contains("[::]", StringComparison.Ordinal);

        if (wildcard)
            throw new InvalidOperationException(
                "0.0.0.0(및 +/*/::) 은 광고 불가 — OHMYAGENT_ADVERTISE_URL 에 실제 접속 가능한 host:port 를 지정하세요.");
    }
}
