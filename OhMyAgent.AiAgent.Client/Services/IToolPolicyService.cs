using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 서버 도구 정책 게이트. 모델의 도구 호출 → [정책 게이트] → 로컬 권한 게이트 → 샌드박스 → 실행 흐름에서
/// 가장 앞단을 담당한다. 모드(cached/realtime)와 목록은 <b>로그인/재로그인 때만</b> 로드해 세션 캐시한다.
/// </summary>
public interface IToolPolicyService
{
    /// <summary>현재 세션의 정책 모드. 미로드 시 기본 Cached.</summary>
    ToolPolicyMode Mode { get; }

    /// <summary>정책이 로드되어 활성인지. 정책 부재(404) 또는 조회 실패면 false.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// 정책을 확인할 수 없는 상태인지(조회 실패 또는 로드 전). true면 모든 도구가 차단된다.
    /// "정책 없음"(404, 전체 허용)과 구분되는 상태다.
    /// </summary>
    bool IsUnavailable { get; }

    /// <summary>
    /// 로그인/재로그인 시 1회 호출 — mode + (cached면) enabled/disabled 목록을 세션 캐시한다.
    /// 404(서버 미구현)는 정상 반환하고 fail-open 상태가 된다.
    /// 그 외 조회 실패는 fail-closed 상태로 전환한 뒤 <see cref="AgentException"/> 을 던진다 —
    /// 호출자는 이 예외를 삼키지 말고 사용자에게 도구가 차단되었음을 알려야 한다.
    /// </summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>도구 실행 직전 게이트 평가. 정책부재→Allow, 판단불가→Deny, cached→로컬 목록, realtime→서버 인가.</summary>
    Task<ToolGateDecision> EvaluateAsync(string toolName, JsonElement args, CancellationToken ct = default);

    /// <summary>
    /// 이 도구를 모델에게 <b>노출(스키마 전송)</b>할지. 정책부재/realtime→노출(전체), 판단불가→비노출,
    /// cached→enabled/disabled 필터. 노출과 실행 게이트를 일관시켜, 비활성 도구는 모델이 아예 보지 못하게 한다.
    /// </summary>
    bool IsExposed(string toolName);
}
