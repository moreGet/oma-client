using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// <see cref="IToolPolicyService"/> 구현. 로그인/재로그인 시 서버에서 정책을 받아 세션 캐시하고,
/// 도구 실행 직전 게이트를 평가한다.
///
/// 정책을 못 받은 경우의 처리가 이 클래스의 핵심이다:
/// <list type="bullet">
///   <item>404(서버 미구현) → 정책 부재 확정 → fail-open. 로컬 권한 게이트·샌드박스가 방어한다.</item>
///   <item>그 외 실패(5xx/타임아웃/오프라인/파싱) → 판단 불가 → <b>fail-closed</b>(전체 차단).
///         뭉뚱그려 허용하면 /tools/policy 하나만 막아 서버 정책 전체를 무력화할 수 있다.</item>
/// </list>
/// </summary>
public sealed class ToolPolicyService(IAgentApiClient api) : IToolPolicyService
{
    /// <summary>정책 부재/활성/판단불가 3-상태. 종전의 bool Loaded 로는 뒤 둘을 구분할 수 없었다.</summary>
    private enum Availability { Absent, Active, Unavailable }

    // 스레드 안전: 스냅샷을 단일 참조로 묶어 통째로 교체(읽기 측은 항상 일관된 스냅샷을 본다).
    private sealed record Snapshot(
        Availability State,
        ToolPolicyMode Mode,
        IReadOnlySet<string>? Enabled,
        IReadOnlySet<string>? Disabled);

    // 조회 실패는 일시적 네트워크 블립일 수 있다. 곧장 전체 차단하면 오탐이 잦으므로 짧게 재시도한 뒤 판정한다.
    private const int FetchAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    // 초기값은 Unavailable — 로드 전에는 정책을 모르므로 차단이 기본값이다(로드 성공/404 시에만 열린다).
    private volatile Snapshot _state = new(Availability.Unavailable, ToolPolicyMode.Cached, null, null);

    public ToolPolicyMode Mode => _state.Mode;

    public bool IsLoaded => _state.State == Availability.Active;

    public bool IsUnavailable => _state.State == Availability.Unavailable;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var fetch = await FetchWithRetryAsync(ct).ConfigureAwait(false);

        switch (fetch.Status)
        {
            case ToolPolicyFetchStatus.NotImplemented:
                _state = new Snapshot(Availability.Absent, ToolPolicyMode.Cached, null, null);
                return;

            case ToolPolicyFetchStatus.Failed:
                _state = new Snapshot(Availability.Unavailable, ToolPolicyMode.Cached, null, null);
                throw new AgentException(
                    "서버 도구 정책을 받지 못했습니다. 안전을 위해 모든 도구를 차단합니다. 재연결 후 다시 시도하세요.");

            default:
                var p = fetch.Policy!;
                var mode = string.Equals(p.Mode, "realtime", StringComparison.OrdinalIgnoreCase)
                    ? ToolPolicyMode.Realtime
                    : ToolPolicyMode.Cached;   // 그 외(누락/"cached"/오타)는 안전한 기본값 Cached.

                _state = new Snapshot(Availability.Active, mode, ToSet(p.Enabled), ToSet(p.Disabled));
                return;
        }
    }

    private async Task<ToolPolicyFetch> FetchWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var fetch = await api.GetToolPolicyAsync(ct).ConfigureAwait(false);

            // 404는 확정 답변이므로 재시도하지 않는다. 성공도 마찬가지.
            if (fetch.Status != ToolPolicyFetchStatus.Failed || attempt >= FetchAttempts)
                return fetch;

            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
        }
    }

    public async Task<ToolGateDecision> EvaluateAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        var state = _state;

        // 판단 불가(조회 실패/미로드) → fail-closed. 정책이 있는데 못 받았을 수 있다.
        if (state.State == Availability.Unavailable)
            return ToolGateDecision.Deny("서버 도구 정책을 확인할 수 없어 차단했습니다");

        // 정책 부재 확정(404) → fail-open. 로컬 권한 게이트+샌드박스가 여전히 방어한다.
        if (state.State == Availability.Absent)
            return ToolGateDecision.Allow();

        // 목록 판정은 mode 와 무관하게 "먼저" 적용한다(disabled 가 enabled 보다 우선).
        //
        // realtime 이라고 이 단계를 건너뛰면, 서버가 스스로 내려준 disabled 목록을 클라가 들고 있으면서도
        // 매 호출 인가 응답에만 의존하게 된다. 인가 엔드포인트가 목록과 어긋나게 allowed:true 를 주면
        // (서버 버그·배포 불일치) 차단해야 할 도구가 실행된다.
        // 명령 정책과 같은 원칙 — 서버는 더 조일 수만 있고, 받은 제약은 로컬에서도 바닥으로 깔린다.
        if (state.Disabled is { } disabled && disabled.Contains(toolName))
            return ToolGateDecision.Deny("서버 정책에서 비활성화됨");

        if (state.Enabled is { } enabled && !enabled.Contains(toolName))
            return ToolGateDecision.Deny("서버 정책에서 허용되지 않음");

        // realtime: 목록을 통과했더라도 매 호출 서버 인가를 한 번 더 받는다(추가 제약).
        if (state.Mode == ToolPolicyMode.Realtime)
        {
            var a = await api.AuthorizeToolAsync(toolName, args, ct).ConfigureAwait(false);
            if (a is null)
                return ToolGateDecision.Deny("정책 서버 응답 없음");   // 실시간 활성 → 안전 우선 fail-closed.
            return a.Allowed
                ? ToolGateDecision.Allow()
                : ToolGateDecision.Deny(a.Reason ?? "서버에서 거부됨");
        }

        return ToolGateDecision.Allow();
    }

    public bool IsExposed(string toolName)
    {
        var state = _state;

        // 판단 불가 → 모델에게 아무 도구도 노출하지 않는다(실행 게이트의 차단과 일관).
        if (state.State == Availability.Unavailable)
            return false;

        // 정책 부재 확정 → 전체 노출(fail-open). realtime → 노출은 전체, 실행 직전 인가로 통제.
        if (state.State == Availability.Absent || state.Mode == ToolPolicyMode.Realtime)
            return true;

        // Cached: 실행 게이트와 동일 규칙 — disabled 우선, enabled 화이트리스트.
        if (state.Disabled is { } disabled && disabled.Contains(toolName))
            return false;
        if (state.Enabled is { } enabled && !enabled.Contains(toolName))
            return false;

        return true;
    }

    /// <summary>도구명 비교는 OrdinalIgnoreCase. null/빈 목록이면 null(제약 없음).</summary>
    private static IReadOnlySet<string>? ToSet(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0)
            return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
            if (!string.IsNullOrWhiteSpace(n))
                set.Add(n);

        return set.Count == 0 ? null : set;
    }
}
