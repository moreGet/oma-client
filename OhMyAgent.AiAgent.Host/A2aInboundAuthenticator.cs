using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// A2A 수신 인증의 모드 분기(스펙 §C-2). anon/token 은 기존 <see cref="A2aAuth"/> 위임(무변경), broker 는
/// 캐시 공개키·자기 agent_id·kid 재취득을 오케스트레이션한다. 순수 검증은 <see cref="A2aBrokerToken.Validate"/>.
/// </summary>
public sealed class A2aInboundAuthenticator(
    A2aOptions opts, IBrokerKeyStore keyStore, IAgentRegistryClient registry)
{
    private const string BearerPrefix = "Bearer ";

    public async Task<bool> AuthorizeAsync(string? authHeader, CancellationToken ct)
    {
        // anon 은 모드/플래그 어느 쪽이든 무인증.
        if (opts.Mode == A2aMode.Anon || opts.AllowAnonymous)
            return true;

        if (opts.Mode == A2aMode.Token)
            return A2aAuth.IsAuthorized(authHeader, opts);   // 기존 공유토큰 상수시간 비교(무변경)

        // Broker 모드.
        return await AuthorizeBrokerAsync(authHeader, ct).ConfigureAwait(false);
    }

    private async Task<bool> AuthorizeBrokerAsync(string? authHeader, CancellationToken ct)
    {
        // 1) 미등록(자기 agent_id 모름) → broker 수신 불가. 설계된 안전 실패(§공유 계약).
        var ownAgentId = keyStore.AgentId;
        if (string.IsNullOrEmpty(ownAgentId))
        {
            AppLog.Warn("A2A", "등록 실패로 broker 수신 불가 — 자기 agent_id 미확정(401).");
            return false;
        }

        // 2) Bearer 추출 → 헤더 kid/alg 만 먼저(서명 전, 키 선택용).
        var jwt = ExtractBearer(authHeader);
        if (jwt is null || !A2aBrokerToken.TryReadKid(jwt, out var tokenKid, out _))
            return false;

        // 3) 캐시 공개키. kid 불일치 or 키 없음 → 1회 재취득(키 회전 추종) 후 재조회.
        if (!keyStore.TryGetKey(out var cachedKid, out var pem) ||
            !string.Equals(cachedKid, tokenKid, StringComparison.Ordinal))
        {
            var refreshed = await TryRefreshKeyAsync(ct).ConfigureAwait(false);
            if (!refreshed || !keyStore.TryGetKey(out cachedKid, out pem) ||
                !string.Equals(cachedKid, tokenKid, StringComparison.Ordinal))
            {
                AppLog.Warn("A2A", $"미지의 kid 토큰 — 재취득 후에도 불일치(401). kid={tokenKid}");
                return false;
            }
        }

        // 4) 순수 검증.
        var verdict = A2aBrokerToken.Validate(jwt, pem, ownAgentId, DateTimeOffset.UtcNow);
        if (verdict != BrokerVerdict.Valid)
        {
            AppLog.Warn("A2A", $"broker 토큰 거부: {verdict}");
            return false;
        }

        return true;
    }

    private async Task<bool> TryRefreshKeyAsync(CancellationToken ct)
    {
        try
        {
            var key = await registry.GetA2aPublicKeyAsync(ct).ConfigureAwait(false);
            keyStore.SetKey(key.Kid, key.PublicKeyPem);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("A2A", $"공개키 재취득 실패: {ex.Message}");
            return false;
        }
    }

    private static string? ExtractBearer(string? authHeader)
    {
        if (authHeader is null || !authHeader.StartsWith(BearerPrefix, StringComparison.Ordinal))
            return null;
        var token = authHeader[BearerPrefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
