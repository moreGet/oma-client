using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// 리스너 모드 등록 생명주기(스펙 §B): register → 공개키 취득 → heartbeat 백그라운드 루프 → deregister.
///
/// graceful degrade: 등록·heartbeat·공개키 실패는 로그만 남기고 throw 하지 않는다 — 레지스트리 없이도
/// A2A 수신·로컬 작업은 계속돼야 한다(등록은 발견 편의일 뿐). broker 모드 미등록 수신만 401(설계된 안전 실패).
/// </summary>
public sealed class AgentRegistryLifecycle(
    IAgentRegistryClient registry, AgentRegistryOptions opts, IBrokerKeyStore keyStore, string modelId)
{
    private const int DefaultHeartbeatSeconds = 15;
    private const int DefaultLeaseSeconds = 45;

    private volatile string? _agentId;
    private int _heartbeatSeconds = DefaultHeartbeatSeconds;
    private Task? _loop;

    /// <summary>register + 공개키 취득(실패는 graceful) 후 heartbeat 루프를 백그라운드로 띄운다.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await TryRegisterAsync(ct).ConfigureAwait(false);
        _loop = Task.Run(() => HeartbeatLoopAsync(ct), CancellationToken.None);
    }

    /// <summary>취소 후 루프 종료를 기다리고, best-effort 로 deregister(취소된 ct 대신 짧은 타임아웃).</summary>
    public async Task StopAsync()
    {
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* 루프는 내부에서 예외를 흡수하지만 방어적으로 무시 */ }
        }

        var agentId = _agentId;
        if (string.IsNullOrEmpty(agentId)) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await registry.DeregisterAsync(agentId, timeout.Token).ConfigureAwait(false); }
        catch { /* best-effort — Ctrl+C 후에도 1회 시도, 실패는 무시 */ }
    }

    private async Task<bool> TryRegisterAsync(CancellationToken ct)
    {
        try
        {
            var req = new RegisterRequest(
                Name: opts.AgentName,
                EndpointUrl: opts.AdvertiseUrl,
                Capabilities: opts.Capabilities,
                Tags: null,
                Model: string.IsNullOrWhiteSpace(modelId) ? null : modelId,
                Version: AppVersion.Semantic);

            var resp = await registry.RegisterAsync(req, ct).ConfigureAwait(false);

            _agentId = resp.AgentId;
            keyStore.SetAgentId(resp.AgentId);
            _heartbeatSeconds = resp.HeartbeatIntervalSeconds > 0 ? resp.HeartbeatIntervalSeconds : DefaultHeartbeatSeconds;
            var lease = resp.LeaseTtlSeconds > 0 ? resp.LeaseTtlSeconds : DefaultLeaseSeconds;

            AppLog.Info("Registry",
                $"등록 성공: agent_id={resp.AgentId}, name={opts.AgentName}, endpoint={opts.AdvertiseUrl}, " +
                $"interval={_heartbeatSeconds}s, lease={lease}s");

            await TryFetchPublicKeyAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 등록 실패는 리스너를 죽이지 않는다 — 루프가 다음 주기에 재시도.
            AppLog.Warn("Registry", $"등록 실패(재시도 예정): {ex.Message}");
            return false;
        }
    }

    private async Task TryFetchPublicKeyAsync(CancellationToken ct)
    {
        try
        {
            var key = await registry.GetA2aPublicKeyAsync(ct).ConfigureAwait(false);
            keyStore.SetKey(key.Kid, key.PublicKeyPem);
            AppLog.Info("Registry", $"브로커 공개키 취득: kid={key.Kid}, alg={key.Alg}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 첫 broker 검증 시 kid-미스로 authenticator 가 재취득하므로 여기 실패는 치명적이지 않음.
            AppLog.Warn("Registry", $"공개키 취득 실패(첫 검증 시 재취득으로 커버): {ex.Message}");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_heartbeatSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            if (ct.IsCancellationRequested) break;

            // 미등록 상태(등록 실패 지속) → 재등록 시도.
            if (string.IsNullOrEmpty(_agentId))
            {
                await TryRegisterAsync(ct).ConfigureAwait(false);
                continue;
            }

            var outcome = await BeatAsync(_agentId!, ct).ConfigureAwait(false);
            switch (RegistryHeartbeatPolicy.Decide(outcome))
            {
                case HeartbeatAction.Continue:
                    break;

                case HeartbeatAction.Reregister:
                    AppLog.Info("Registry", "리스 만료/소유자 아님 — 재등록으로 자가 치유.");
                    _agentId = null;
                    keyStore.SetAgentId(string.Empty);   // 재등록 전까지 broker 수신 401(안전)
                    await TryRegisterAsync(ct).ConfigureAwait(false);
                    break;

                case HeartbeatAction.Backoff:
                    // 로그만 — 다음 주기 재시도(프로세스 유지).
                    break;
            }
        }
    }

    private async Task<HeartbeatOutcome> BeatAsync(string agentId, CancellationToken ct)
    {
        try
        {
            await registry.HeartbeatAsync(agentId, ct).ConfigureAwait(false);
            return HeartbeatOutcome.Ok;
        }
        catch (AgentLeaseExpiredException)
        {
            return HeartbeatOutcome.LeaseExpired;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Registry", $"heartbeat 실패(다음 주기 재시도): {ex.Message}");
            return HeartbeatOutcome.TransientFailure;
        }
    }
}
