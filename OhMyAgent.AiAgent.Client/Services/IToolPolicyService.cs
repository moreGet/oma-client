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

    /// <summary>정책이 로드되어 활성인지. false면 정책 부재 → fail-open(전체 허용).</summary>
    bool IsLoaded { get; }

    /// <summary>로그인/재로그인 시 1회 호출 — mode + (cached면) enabled/disabled 목록을 세션 캐시한다.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>도구 실행 직전 게이트 평가. 미로드→Allow, cached→로컬 목록, realtime→서버 인가.</summary>
    Task<ToolGateDecision> EvaluateAsync(string toolName, JsonElement args, CancellationToken ct = default);

    /// <summary>
    /// 이 도구를 모델에게 <b>노출(스키마 전송)</b>할지. 미로드/realtime→노출(전체), cached→enabled/disabled 필터.
    /// 노출과 실행 게이트를 일관시켜, 비활성 도구는 모델이 아예 보지 못하게 한다.
    /// </summary>
    bool IsExposed(string toolName);
}
