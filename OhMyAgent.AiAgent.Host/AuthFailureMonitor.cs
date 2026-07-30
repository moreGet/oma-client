using System;
using System.Threading;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// 런타임 인증 실패(401/403)를 세어 "토큰이 죽었다"를 판정한다.
///
/// 왜 필요한가: 헤드리스는 토큰을 env 로 주입받고 갱신 경로가 없다(재로그인 UI 부재). 토큰이 만료되면
/// 모든 요청이 401 로 실패하는데, 기존 코드는 그것을 stderr 한 줄로 흘려보내고 프로세스를 유지했다.
/// 결과는 <b>살아있지만 아무 일도 못 하는 좀비</b> — 종료 코드로도 드러나지 않아 systemd 가 재시작을
/// 걸 수 없었다. 이 클래스가 그 상태를 종료 신호로 바꾼다.
///
/// 왜 즉시 종료가 아니라 임계값인가: 서버 재기동·키 회전 순간에 401 이 한 번 스칠 수 있다. 그때마다
/// 데몬이 죽으면 재시작 폭풍이 된다. 연속 <see cref="DefaultThreshold"/>회를 봐야 확정하고, 성공이
/// 한 번이라도 끼면 카운터를 초기화한다(연속이 아니면 토큰은 살아있다는 뜻).
///
/// I/O·시계 없는 순수 판정이라 단위 테스트가 전 경로를 잠근다(RegistryHeartbeatPolicy 와 동일 패턴).
/// </summary>
public sealed class AuthFailureMonitor(int threshold = AuthFailureMonitor.DefaultThreshold)
{
    /// <summary>기본 임계값 — 일시적 401 을 흡수하되 죽은 토큰은 빠르게 확정하는 절충값.</summary>
    public const int DefaultThreshold = 3;

    private readonly int _threshold = threshold > 0 ? threshold : DefaultThreshold;
    private readonly Lock _gate = new();

    private int _consecutive;
    private bool _fatal;

    /// <summary>연속 인증 실패가 임계값에 도달했는가. 한 번 true 가 되면 되돌아가지 않는다.</summary>
    public bool IsFatal
    {
        get { lock (_gate) return _fatal; }
    }

    /// <summary>현재 연속 실패 횟수(진단·테스트용).</summary>
    public int ConsecutiveFailures
    {
        get { lock (_gate) return _consecutive; }
    }

    /// <summary>
    /// 인증 실패 1건 기록. 임계값에 도달해 <b>이번 호출로</b> 확정됐으면 true —
    /// 호출자는 그때 한 번만 종료 절차를 밟는다(중복 로그·중복 취소 방지).
    /// </summary>
    public bool RecordFailure()
    {
        lock (_gate)
        {
            if (_fatal) return false;   // 이미 확정 — 재진입 시 종료 절차를 두 번 돌리지 않는다.

            _consecutive++;
            if (_consecutive < _threshold) return false;

            _fatal = true;
            return true;
        }
    }

    /// <summary>인증이 통한 요청 1건 기록 — 연속 카운터를 초기화한다.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            if (!_fatal) _consecutive = 0;
        }
    }

    /// <summary>
    /// 서버가 준 오류 코드가 인증 실패인가. SSE 오류 이벤트는 예외가 아니라 코드 문자열로 오므로
    /// (<c>AgentApiClient.ReadErrorAsync</c>: HTTP 상태 → <c>http_401</c>, 서버 envelope 코드가 있으면 그 값)
    /// 양쪽 표기를 모두 인식해야 한다.
    /// </summary>
    public static bool IsAuthErrorCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        (code.Equals("http_401", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("http_403", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("unauthorized", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("forbidden", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("invalid_token", StringComparison.OrdinalIgnoreCase) ||
         code.Equals("token_expired", StringComparison.OrdinalIgnoreCase));
}
