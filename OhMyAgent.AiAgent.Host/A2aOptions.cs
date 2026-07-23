using System;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// A2A(에이전트-투-에이전트) 서버 모드 설정. 환경 변수에서 조립하며, 리스너 기동 전
/// <see cref="ValidateOrThrow"/> 로 fail-fast 한다(무인증 무인 호스트 방지, 스펙 §1-E/§1-G).
///
/// env → 필드:
///   OHMYAGENT_LISTEN            → Prefix          (설정 시 서버 모드. 예 http://0.0.0.0:8080/)
///   OHMYAGENT_A2A_TOKEN         → Token           (Bearer 토큰. 리스너 모드 필수)
///   OHMYAGENT_A2A_ALLOW_ANON    → AllowAnonymous  (1 이면 무인증 허용)
///   OHMYAGENT_A2A_MAX_CONCURRENCY → MaxConcurrency (기본 4)
///   OHMYAGENT_A2A_MAX_HOPS      → MaxHops         (기본 3)
///   OHMYAGENT_A2A_EXPOSE_THINKING → ExposeThinking (1 이면 thinking_delta 노출)
///   OHMYAGENT_A2A_MODEL_ID      → AdvertisedModelId (없으면 settings.ModelId 폴백)
/// </summary>
public sealed record A2aOptions
{
    /// <summary>HttpListener prefix. 반드시 '/' 로 끝난다(정규화됨). 빈 문자열이면 서버 모드 아님.</summary>
    public string Prefix { get; init; } = string.Empty;

    public string? Token { get; init; }

    public bool AllowAnonymous { get; init; }

    public int MaxConcurrency { get; init; } = 4;

    public int MaxHops { get; init; } = 3;

    public bool ExposeThinking { get; init; }

    public string AdvertisedModelId { get; init; } = string.Empty;

    /// <summary>Prefix 가 비어 있지 않으면 A2A 서버 모드.</summary>
    public bool IsListenMode => !string.IsNullOrWhiteSpace(Prefix);

    /// <summary>실제 프로세스 환경 변수에서 조립.</summary>
    public static A2aOptions FromEnvironment(string? defaultModelId = null)
        => FromEnvironment(Environment.GetEnvironmentVariable, defaultModelId);

    /// <summary>테스트 주입 가능한 조립(전역 env 오염 없이 결정적 검증).</summary>
    public static A2aOptions FromEnvironment(Func<string, string?> getEnv, string? defaultModelId = null)
    {
        string Get(string name) => getEnv(name)?.Trim() ?? string.Empty;

        int Int(string name, int fallback)
            => int.TryParse(Get(name), out var v) && v > 0 ? v : fallback;

        bool Flag(string name)
        {
            var v = Get(name);
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        var listen = Get("OHMYAGENT_LISTEN");
        var token = Get("OHMYAGENT_A2A_TOKEN");
        var modelId = Get("OHMYAGENT_A2A_MODEL_ID");
        if (string.IsNullOrEmpty(modelId))
            modelId = defaultModelId?.Trim() ?? string.Empty;

        return new A2aOptions
        {
            Prefix = NormalizePrefix(listen),
            Token = string.IsNullOrEmpty(token) ? null : token,
            AllowAnonymous = Flag("OHMYAGENT_A2A_ALLOW_ANON"),
            MaxConcurrency = Int("OHMYAGENT_A2A_MAX_CONCURRENCY", 4),
            MaxHops = Int("OHMYAGENT_A2A_MAX_HOPS", 3),
            ExposeThinking = Flag("OHMYAGENT_A2A_EXPOSE_THINKING"),
            AdvertisedModelId = modelId,
        };
    }

    /// <summary>HttpListener 규칙: prefix 는 '/' 로 끝나야 한다. 빈 값은 그대로 둔다(서버 모드 아님).</summary>
    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    /// <summary>
    /// 리스너 기동 전 안전 검증. 서버 모드인데 토큰이 없고 무인증 옵트인도 아니면 기동을 거부한다
    /// (무인증 무인 호스트 사고 방지, 스펙 §1-E).
    /// </summary>
    public void ValidateOrThrow()
    {
        if (!IsListenMode) return;

        if (AllowAnonymous)
        {
            AppLog.Warn("A2A", "무인증 모드(OHMYAGENT_A2A_ALLOW_ANON=1) — 모든 요청이 토큰 없이 허용됩니다. 폐쇄망 전용.");
            return;
        }

        if (string.IsNullOrEmpty(Token))
            throw new InvalidOperationException(
                "A2A 서버 모드(OHMYAGENT_LISTEN)에는 OHMYAGENT_A2A_TOKEN 이 필요합니다. " +
                "토큰 없이 열려면 OHMYAGENT_A2A_ALLOW_ANON=1 을 명시하세요(폐쇄망 전용).");
    }
}
