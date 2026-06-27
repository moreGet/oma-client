using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 한 윈도우(일/주/월)의 토큰 쿼터 상태. GET /api/v1/me/quota 응답의 windows[] 항목.
/// limit=0 이면 unlimited=true(무제한), remaining=max(0,limit-used). 직렬화: AgentJson.Options(snake_case).
/// </summary>
public sealed record QuotaWindow(
    [property: JsonPropertyName("window")]            string Window,   // "day"|"week"|"month"
    [property: JsonPropertyName("period")]            string Period,
    [property: JsonPropertyName("limit")]             int Limit,
    [property: JsonPropertyName("used")]              int Used,
    [property: JsonPropertyName("remaining")]         int Remaining,
    [property: JsonPropertyName("unlimited")]         bool Unlimited,
    [property: JsonPropertyName("percent_used")]      double PercentUsed,
    [property: JsonPropertyName("percent_remaining")] double PercentRemaining);

/// <summary>
/// 토큰 쿼터 조회 응답. windows 는 항상 day→week→month 3개 순서.
/// 미응답/파싱실패는 호출자가 graceful 하게 null 로 처리한다.
/// </summary>
public sealed record QuotaResponse(
    [property: JsonPropertyName("windows")] IReadOnlyList<QuotaWindow> Windows);
