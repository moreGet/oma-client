using System;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 서버 인증 실패(401/403) — 주입된 JWT 가 만료·폐기·무효라는 뜻이다.
///
/// <see cref="AgentLeaseExpiredException"/>(404)과 반드시 구분해야 한다: 404 는 재-register 로 자가
/// 치유되지만, 401/403 은 자격증명 자체가 죽은 것이라 <b>같은 토큰으로 재시도해도 영원히 실패</b>한다.
/// 헤드리스는 토큰을 env 로 주입받고 갱신 경로가 없으므로(재로그인 UI 부재), 이 예외는 재시도가 아니라
/// 프로세스 종료로 이어져야 한다 — 그래야 systemd 가 새 토큰으로 재시작할 수 있다.
///
/// AgentException 이 sealed 라 상속할 수 없어 Exception 을 직접 상속한다
/// (<see cref="AgentLeaseExpiredException"/> 와 동일한 이유·패턴).
/// </summary>
public sealed class AgentUnauthorizedException(string operation, int statusCode)
    : Exception($"서버 인증 실패({statusCode}) — 토큰이 만료·무효합니다: {operation}")
{
    /// <summary>실패한 작업 이름(로그·진단용).</summary>
    public string Operation { get; } = operation;

    /// <summary>401 또는 403.</summary>
    public int StatusCode { get; } = statusCode;
}
