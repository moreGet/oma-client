using System;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// 헤드리스 승인 정책 배선(스펙 §1-E).
/// 기본 "deny"(안전): 핸들러 미등록 → PermissionService 가 gated risk 를 Deny.
/// 옵트인 "auto"(자율): gated 도구를 자동 승인하되 감사 로그를 남긴다.
/// 어느 경우든 SecurityValidator + ToolPolicyService 방어선은 유지된다(defense-in-depth).
/// </summary>
public static class HeadlessPermissionPolicy
{
    public static void Apply(IPermissionService permissions, string approvalMode)
    {
        if (!string.Equals(approvalMode, "auto", StringComparison.OrdinalIgnoreCase))
            return;   // 핸들러 미등록 → gated=Deny (PermissionService 기본 시맨틱)

        // 자율 실행: gated 도구를 자동 승인하되 반드시 감사 로그를 남긴다.
        permissions.SetApprovalHandler((call, risk, ct) =>
        {
            AppLog.Info("HeadlessApproval", $"자동 승인: {call.Name} (risk={risk})");
            return Task.FromResult(PermissionDecision.Allow);
        });
    }
}
