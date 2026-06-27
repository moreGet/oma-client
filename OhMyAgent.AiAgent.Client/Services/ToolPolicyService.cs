using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// <see cref="IToolPolicyService"/> 구현. 로그인/재로그인 시 서버에서 정책을 받아 세션 캐시하고,
/// 도구 실행 직전 게이트를 평가한다. 서버 미구현/오프라인이면 정책 부재로 보고 fail-open한다
/// (로컬 권한 게이트·샌드박스가 여전히 방어).
/// </summary>
public sealed class ToolPolicyService(IAgentApiClient api) : IToolPolicyService
{
    // 스레드 안전: 스냅샷을 단일 참조로 묶어 통째로 교체(읽기 측은 항상 일관된 스냅샷을 본다).
    private sealed record Snapshot(
        bool Loaded,
        ToolPolicyMode Mode,
        IReadOnlySet<string>? Enabled,
        IReadOnlySet<string>? Disabled);

    private volatile Snapshot _state = new(false, ToolPolicyMode.Cached, null, null);

    public ToolPolicyMode Mode => _state.Mode;

    public bool IsLoaded => _state.Loaded;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var p = await api.GetToolPolicyAsync(ct).ConfigureAwait(false);
        if (p is null)
        {
            // 정책 부재(엔드포인트 없음/오프라인) → 미로드 상태로 둔다(fail-open).
            _state = new Snapshot(false, ToolPolicyMode.Cached, null, null);
            return;
        }

        var mode = string.Equals(p.Mode, "realtime", StringComparison.OrdinalIgnoreCase)
            ? ToolPolicyMode.Realtime
            : ToolPolicyMode.Cached;   // 그 외(누락/"cached"/오타)는 안전한 기본값 Cached.

        var enabled  = ToSet(p.Enabled);
        var disabled = ToSet(p.Disabled);

        _state = new Snapshot(true, mode, enabled, disabled);
    }

    public async Task<ToolGateDecision> EvaluateAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        var state = _state;

        // 정책 부재 → fail-open. 로컬 권한 게이트+샌드박스가 여전히 방어한다.
        if (!state.Loaded)
            return ToolGateDecision.Allow();

        if (state.Mode == ToolPolicyMode.Realtime)
        {
            var a = await api.AuthorizeToolAsync(toolName, args, ct).ConfigureAwait(false);
            if (a is null)
                return ToolGateDecision.Deny("정책 서버 응답 없음");   // 실시간 활성 → 안전 우선 fail-closed.
            return a.Allowed
                ? ToolGateDecision.Allow()
                : ToolGateDecision.Deny(a.Reason ?? "서버에서 거부됨");
        }

        // Cached: disabled가 enabled보다 우선.
        if (state.Disabled is { } disabled && disabled.Contains(toolName))
            return ToolGateDecision.Deny("서버 정책에서 비활성화됨");

        if (state.Enabled is { } enabled && !enabled.Contains(toolName))
            return ToolGateDecision.Deny("서버 정책에서 허용되지 않음");

        return ToolGateDecision.Allow();
    }

    public bool IsExposed(string toolName)
    {
        var state = _state;

        // 미로드(정책 부재) → 전체 노출(fail-open). realtime → 노출은 전체, 실행 직전 인가로 통제.
        if (!state.Loaded || state.Mode == ToolPolicyMode.Realtime)
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
