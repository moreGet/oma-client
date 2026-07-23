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

    /// <summary>
    /// ToolRisk 상으로는 ReadOnly 지만 승인을 받아야 하는 도구.
    ///
    /// ToolRisk 는 "머신을 망가뜨리는가"를 재는 축이라 "무인으로 실행해도 괜찮은가"와 다르다.
    /// 이 셋은 아무것도 파괴하지 않지만 결과가 그대로 서버로 전송되는 정보 유출 경로다 —
    /// 화면 전체 캡처, 클립보드(비밀번호를 복사해 둔 상태일 수 있다), 환경변수(토큰·프록시 자격증명).
    /// ReadOnly 로 두면 Manual 모드에서도 사용자 모르게 실행된다.
    ///
    /// 스키마상 Risk 는 ReadOnly 로 유지한다 — 모델에게 "위험한 도구"로 보이게 하려는 게 아니라
    /// 사용자에게 확인을 받으려는 것이므로, 게이트에서만 승격한다.
    /// </summary>
    private static readonly HashSet<string> SensitiveReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenshot", "clipboard_read", "get_environment",
    };

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

        var sensitive = SensitiveReadOnlyTools.Contains(call.Name);

        // ReadOnly 자동 허용 — 단, 정보 유출 경로인 민감 도구는 제외(위 SensitiveReadOnlyTools 참조).
        if (risk == ToolRisk.ReadOnly && !sensitive)
            return PermissionDecision.Allow;

        // AutoSafe: "안전한 것은 자동" — 되돌릴 수 있는 파일 쓰기까지 자동 허용하고,
        // 되돌리기 어려운 Execute/Destructive 와 민감 도구만 확인받는다.
        // (이 분기가 없던 동안 AutoSafe 는 Manual 과 완전히 동일하게 동작해 선택지가 무의미했다.)
        if (mode == PermissionMode.AutoSafe && risk == ToolRisk.Write && !sensitive)
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
