using System;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Host;

/// <summary>수신 인증 모드(§공유 계약 A2A 인증). broker=서버 브로커 ES256 토큰, token=공유 Bearer, anon=무인증.</summary>
public enum A2aMode { Broker, Token, Anon }

/// <summary>
/// A2A(에이전트-투-에이전트) 서버 모드 설정. 환경 변수에서 조립하며, 리스너 기동 전
/// <see cref="ValidateOrThrow"/> 로 fail-fast 한다(무인증 무인 호스트 방지, 스펙 §1-E/§1-G).
///
/// env → 필드:
///   OHMYAGENT_LISTEN            → Prefix          (설정 시 서버 모드. 예 http://0.0.0.0:8080/)
///   OHMYAGENT_A2A_TOKEN         → Token           (Bearer 토큰. token 모드 필수)
///   OHMYAGENT_A2A_ALLOW_ANON    → AllowAnonymous  (1 이면 무인증 허용)
///   OHMYAGENT_A2A_MAX_CONCURRENCY → MaxConcurrency (기본 4)
///   OHMYAGENT_A2A_MAX_HOPS      → MaxHops         (기본 3)
///   OHMYAGENT_A2A_EXPOSE_THINKING → ExposeThinking (1 이면 thinking_delta 노출)
///   OHMYAGENT_A2A_MODEL_ID      → AdvertisedModelId (없으면 settings.ModelId 폴백)
///   OHMYAGENT_A2A_MODE          → Mode            (broker/token/anon. 미설정: 레지스트리 on→broker, off→token)
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

    /// <summary>수신 인증 모드. 미설정 기본: 레지스트리 on→Broker, off→Token(기존 token 경로 무회귀).</summary>
    public A2aMode Mode { get; init; } = A2aMode.Token;

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

        var prefix = NormalizePrefix(listen);
        var anon = Flag("OHMYAGENT_A2A_ALLOW_ANON");
        var registryOn = IsRegistryEnabled(getEnv, prefix);
        var mode = ParseMode(Get("OHMYAGENT_A2A_MODE"), registryOn, anon);

        return new A2aOptions
        {
            Prefix = prefix,
            Token = string.IsNullOrEmpty(token) ? null : token,
            AllowAnonymous = anon,
            MaxConcurrency = Int("OHMYAGENT_A2A_MAX_CONCURRENCY", 4),
            MaxHops = Int("OHMYAGENT_A2A_MAX_HOPS", 3),
            ExposeThinking = Flag("OHMYAGENT_A2A_EXPOSE_THINKING"),
            AdvertisedModelId = modelId,
            Mode = mode,
        };
    }

    /// <summary>
    /// 레지스트리 등록 사용 여부(단일 env 소스). 리스너 모드일 때만 유효하며 OHMYAGENT_REGISTRY 가
    /// off/0/false 가 아니면 on(미설정 기본 on). AgentRegistryOptions 와 동일 규칙을 공유한다.
    /// </summary>
    public static bool IsRegistryEnabled(Func<string, string?> getEnv, string normalizedPrefix)
    {
        if (string.IsNullOrWhiteSpace(normalizedPrefix)) return false;   // 리스너 모드 아님 → 등록 무의미
        var raw = getEnv("OHMYAGENT_REGISTRY")?.Trim().ToLowerInvariant() ?? string.Empty;
        return raw is not ("off" or "0" or "false" or "no");
    }

    /// <summary>OHMYAGENT_A2A_MODE 파싱. ANON 플래그가 서면 anon 이 우선(anon 동치). 미설정은 레지스트리 기준 기본.</summary>
    private static A2aMode ParseMode(string raw, bool registryOn, bool anonFlag)
    {
        if (anonFlag) return A2aMode.Anon;
        return raw.ToLowerInvariant() switch
        {
            "broker" => A2aMode.Broker,
            "token"  => A2aMode.Token,
            "anon"   => A2aMode.Anon,
            _        => registryOn ? A2aMode.Broker : A2aMode.Token,
        };
    }

    /// <summary>HttpListener 규칙: prefix 는 '/' 로 끝나야 한다. 빈 값은 그대로 둔다(서버 모드 아님).</summary>
    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    /// <summary>
    /// 리스너 기동 전 안전 검증. 서버 모드인데 인증 수단이 전혀 없으면 기동을 거부한다
    /// (무인증 무인 호스트 사고 방지, 스펙 §1-E).
    ///
    /// 모드별 요구:
    /// - Broker: 서버 발급 ES256 토큰을 공개키로 검증한다 — **공유 토큰 불필요**(자기 agent_id 는 등록 성공으로 확보).
    /// - Token : 공유 Bearer(OHMYAGENT_A2A_TOKEN) 필수.
    /// - Anon  : 무인증(폐쇄망 전용 옵트인).
    /// </summary>
    public void ValidateOrThrow()
    {
        if (!IsListenMode) return;

        if (AllowAnonymous || Mode == A2aMode.Anon)
        {
            AppLog.Warn("A2A", "무인증 모드(OHMYAGENT_A2A_ALLOW_ANON=1) — 모든 요청이 토큰 없이 허용됩니다. 폐쇄망 전용.");
            return;
        }

        // 브로커 모드는 서버가 서명한 단명 ES256 토큰을 쓴다 → 공유 토큰이 없어도 정상.
        if (Mode == A2aMode.Broker) return;

        if (string.IsNullOrEmpty(Token))
            throw new InvalidOperationException(
                "A2A token 모드(OHMYAGENT_LISTEN + OHMYAGENT_A2A_MODE=token)에는 OHMYAGENT_A2A_TOKEN 이 필요합니다. " +
                "레지스트리 브로커를 쓰려면 OHMYAGENT_A2A_MODE=broker(레지스트리 on 시 기본), " +
                "토큰 없이 열려면 OHMYAGENT_A2A_ALLOW_ANON=1 을 명시하세요(폐쇄망 전용).");
    }
}
