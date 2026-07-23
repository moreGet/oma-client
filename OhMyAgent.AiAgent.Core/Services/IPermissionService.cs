using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IPermissionService
{
    /// <summary>도구 호출에 대한 결정을 반환. Manual 모드에서는 승인 핸들러를 await 할 수 있다.</summary>
    Task<PermissionDecision> RequestAsync(ToolCall call, ToolRisk risk, ToolContext ctx, CancellationToken ct = default);

    /// <summary>ViewModel 이 인라인 승인 카드를 띄우고 사용자 선택을 반환하는 콜백을 등록.</summary>
    void SetApprovalHandler(Func<ToolCall, ToolRisk, CancellationToken, Task<PermissionDecision>> handler);

    /// <summary>세션 AlwaysAllow 부여를 초기화 (새 세션 시작 등).</summary>
    void ClearSessionRules();
}
