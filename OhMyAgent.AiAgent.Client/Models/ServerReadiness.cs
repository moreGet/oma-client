namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 서버 사용 준비 상태. "연결됨(/health)"과 "인증됨(로그인)"을 구분한다.
/// /health 는 Public 이라 200 이어도 로그인 전에는 보호된 엔드포인트가 401(missing bearer token)을 낸다.
/// </summary>
public enum ServerReadiness
{
    /// <summary>서버에 도달할 수 없음 (/health 실패).</summary>
    Disconnected,

    /// <summary>서버는 연결되나 인증 토큰이 없거나 만료됨 → 로그인 필요.</summary>
    Unauthenticated,

    /// <summary>연결 + 인증 모두 정상 — 사용 가능.</summary>
    Ready
}
