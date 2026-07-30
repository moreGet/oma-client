using System;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 서버 원문 에러(영어·기술적 문구)를 사용자 친화적인 한국어 안내로 변환한다.
/// **서버 message 원문은 사용자에게 그대로 노출하지 않는다** — 상태/코드 기준으로 정해진 문구로 감싼다
/// (API-SPEC §클라이언트 처리 가이드). 일부 유용한 정보(쿼터 기간)는 원문에서 해석해 한국어로 반영한다.
/// </summary>
public static class UserErrorMessages
{
    /// <summary>
    /// 에이전트/채팅 에러 코드를 사용자 안내로 변환. 코드 형태: 사전(pre-stream) "http_429",
    /// 스트림 중 "rate_limited" 등. rawMessage는 노출하지 않되 쿼터 기간/세션 캡 판별에만 활용.
    /// </summary>
    public static string ForAgentError(string? code, string? rawMessage)
    {
        var c = code ?? string.Empty;
        var m = rawMessage ?? string.Empty;

        // 429 — 토큰 쿼터 / 세션 저장 캡 초과.
        if (ContainsAny(c, "429", "rate_limited", "too_many"))
        {
            if (ContainsAny(m, "session storage", "session limit", "storage limit"))
                return "저장 가능한 대화 수를 초과했습니다. 기존 대화를 정리한 뒤 다시 시도해 주세요.";

            var window = QuotaWindowKorean(m);
            return window is null
                ? "사용 한도를 초과했습니다. 잠시 후 다시 시도하거나 관리자에게 문의해 주세요."
                : $"{window} 사용 한도를 초과했습니다. 한도가 초기화된 뒤 다시 시도하거나 관리자에게 문의해 주세요.";
        }

        // 401 — 인증 만료/무효.
        // 스트리밍(채팅) 경로는 401 을 ClassifyAgentError 가 먼저 낚아채 재로그인으로 보내므로 여기까지
        // 오지 않는다. 그러나 **도구 경로**(generate_image 등)는 실패 사유를 그대로 모델·사용자에게
        // 보여 주고 로그아웃을 유발하지 않는다 — 여기서 401 을 구분하지 않으면 "일시적인 서버 오류"로
        // 뭉개져 사용자는 토큰이 만료된 사실을 모른 채 재시도만 반복한다(모델도 같은 호출을 다시 한다).
        if (ContainsAny(c, "401", "unauthorized"))
            return "로그인이 만료되었습니다. 다시 로그인한 뒤 시도해 주세요.";

        // 403 — 권한 부족.
        if (ContainsAny(c, "403", "forbidden"))
            return "이 작업을 수행할 권한이 없습니다.";

        // 404 — 리소스 없음(활성 모델 없음 등).
        if (ContainsAny(c, "404", "not_found"))
        {
            if (ContainsAny(m, "llm provider", "no active", "provider"))
                return "현재 사용 가능한 AI 모델이 없습니다. 관리자에게 문의해 주세요.";
            return "요청한 정보를 찾을 수 없습니다.";
        }

        // 400 — 입력 오류.
        if (ContainsAny(c, "400", "bad_request"))
            return "요청에 문제가 있어 처리하지 못했습니다. 입력 내용을 확인해 주세요.";

        // 502 — 업스트림(LLM) 오류.
        if (ContainsAny(c, "502", "bad_gateway"))
            return "AI 서버와의 연결에 문제가 발생했습니다. 잠시 후 다시 시도해 주세요.";

        // 500 / backend_error / 그 외 — 일반 서버 오류.
        return "일시적인 서버 오류가 발생했습니다. 잠시 후 다시 시도해 주세요.";
    }

    /// <summary>로그인 실패를 사용자 안내로 변환(서버 원문 대신).</summary>
    public static string ForLogin(string? code, string? rawMessage)
    {
        var c = code ?? string.Empty;
        var m = rawMessage ?? string.Empty;

        if (ContainsAny(c, "429", "rate_limited", "too_many"))
            return "로그인 시도가 많습니다. 잠시 후 다시 시도해 주세요.";
        if (ContainsAny(c, "400", "bad_request"))
            return "아이디와 비밀번호를 모두 입력해 주세요.";
        if (ContainsAny(c, "401", "403", "unauthorized", "forbidden")
            || ContainsAny(m, "invalid", "incorrect", "password", "credential", "mismatch"))
            return "아이디 또는 비밀번호가 올바르지 않습니다.";
        return "로그인에 실패했습니다. 잠시 후 다시 시도해 주세요.";
    }

    /// <summary>쿼터 원문에서 어느 기간 한도인지 한국어로 추출(없으면 null).</summary>
    private static string? QuotaWindowKorean(string message)
    {
        if (ContainsAny(message, "daily", "day")) return "오늘";
        if (ContainsAny(message, "weekly", "week")) return "이번 주";
        if (ContainsAny(message, "monthly", "month")) return "이번 달";
        return null;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
