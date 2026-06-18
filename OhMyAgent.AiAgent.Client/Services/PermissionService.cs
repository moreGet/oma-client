using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class PermissionService(ISettingsService settings) : IPermissionService
{
    private readonly HashSet<string> _alwaysAllow = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Func<ToolCall, ToolRisk, CancellationToken, Task<PermissionDecision>>? _handler;

    public void SetApprovalHandler(Func<ToolCall, ToolRisk, CancellationToken, Task<PermissionDecision>> handler)
        => _handler = handler;

    public void ClearSessionRules()
    {
        lock (_gate)
            _alwaysAllow.Clear();
    }

    public async Task<PermissionDecision> RequestAsync(ToolCall call, ToolRisk risk, ToolContext ctx, CancellationToken ct = default)
    {
        var mode = settings.Current.PermissionMode;

        // FullAuto: 항상 허용 (run_command 내부 SecurityValidator 블랙리스트는 별도 적용).
        if (mode == PermissionMode.FullAuto)
            return PermissionDecision.Allow;

        // ReadOnly 는 Manual/AutoSafe 모두 자동 허용 (읽기는 안전).
        if (risk == ToolRisk.ReadOnly)
            return PermissionDecision.Allow;

        // 세션 AlwaysAllow 확인 (도구명 단위).
        lock (_gate)
        {
            if (_alwaysAllow.Contains(call.Name))
                return PermissionDecision.Allow;
        }

        // 핸들러 없으면 (headless) gated risk 는 거부.
        if (_handler is null)
            return PermissionDecision.Deny;

        var decision = await _handler(call, risk, ct).ConfigureAwait(false);

        if (decision == PermissionDecision.AlwaysAllow)
        {
            lock (_gate)
                _alwaysAllow.Add(call.Name);
            return PermissionDecision.Allow;
        }

        return decision;
    }
}
