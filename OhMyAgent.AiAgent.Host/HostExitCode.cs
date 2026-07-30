namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// 프로세스 종료 코드. 헤드리스는 사람이 화면을 보고 있지 않으므로, 실패 사유를 <b>종료 코드로</b>
/// 드러내야 systemd 가 재시작·경보를 걸 수 있다. 로그만 찍고 계속 도는 "좀비" 상태를 만들지 않는 것이 목적.
///
/// 운영 규약: <see cref="AuthFailure"/>(77)·<see cref="ConfigError"/>(78)는 사람 개입 없이는 재시작해도
/// 같은 실패를 반복한다(각각 새 토큰·유닛 파일 수정 필요). <see cref="ServerUnreachable"/>(69)만
/// 재시작으로 회복 가능하다. 값은 sysexits.h 관례를 따른다.
/// </summary>
public static class HostExitCode
{
    /// <summary>정상 종료.</summary>
    public const int Ok = 0;

    /// <summary>서버 미도달(네트워크·주소 오류). 재시작으로 회복 가능.</summary>
    public const int ServerUnreachable = 69;   // EX_UNAVAILABLE

    /// <summary>설정 오류(필수 env 누락 등). 재시작해도 동일 — 사람이 유닛 파일을 고쳐야 한다.</summary>
    public const int ConfigError = 78;         // EX_CONFIG

    /// <summary>인증 실패(토큰 만료·무효·폐기). 새 토큰을 주입해야 회복된다.</summary>
    public const int AuthFailure = 77;         // EX_NOPERM
}
