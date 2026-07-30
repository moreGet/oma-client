using System;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// <see cref="AuthFailureMonitor"/>(순수 판정)의 I/O 껍데기 — 로깅·사용자 안내·종료 촉발을 모은다.
/// 인증 실패를 관측하는 지점이 셋(레지스트리 생명주기 · 표준입력 세션 · A2A 수신 세션)이라, 판정과
/// "확정 시 무엇을 할지"를 한 곳에 두지 않으면 임계값·메시지가 갈라진다.
///
/// 판정은 <see cref="AuthFailureMonitor"/> 에, 부수효과는 여기에 — 기존 순수/껍데기 분리 관례를 따른다.
/// </summary>
public sealed class AuthFailureReporter(AuthFailureMonitor monitor, Action onFatal)
{
    /// <summary>연속 실패가 임계값에 도달해 종료가 확정됐는가.</summary>
    public bool IsFatal => monitor.IsFatal;

    /// <summary>인증이 통한 요청 1건 — 연속 카운터를 초기화한다.</summary>
    public void RecordSuccess() => monitor.RecordSuccess();

    /// <summary>
    /// 인증 실패 1건 관측. 임계값 도달 시 <b>한 번만</b> 안내를 남기고 종료 콜백을 호출한다.
    /// 임계값 전이면 경고만 남기고 계속 — 서버 재기동·키 회전 중의 일시적 401 을 흡수하기 위함이다.
    /// </summary>
    public void Report(string source, string detail)
    {
        if (!monitor.RecordFailure())
        {
            AppLog.Warn(source, $"인증 실패({monitor.ConsecutiveFailures}/{AuthFailureMonitor.DefaultThreshold}) — {detail}");
            return;
        }

        AppLog.Error(source,
            $"인증 실패가 연속 {AuthFailureMonitor.DefaultThreshold}회 — 새 토큰 없이는 회복 불가. 프로세스를 종료합니다.");
        Console.Error.WriteLine(
            "⚠ 서버 인증 실패 — OHMYAGENT_AUTH_TOKEN 이 만료·무효합니다. 새 토큰으로 재시작하세요.");
        onFatal();
    }

    /// <summary>
    /// 오케스트레이터 오류 이벤트를 관측한다. 인증 코드면 실패로 기록하고 true 를 돌려준다
    /// (호출자가 세션 루프를 접을지 판단). 그 외 오류는 무시 — 도구 오류·모델 오류는 인증과 무관하다.
    /// </summary>
    public bool ObserveErrorCode(string source, string? code, string message)
    {
        if (!AuthFailureMonitor.IsAuthErrorCode(code)) return false;

        Report(source, $"{code}: {message}");
        return true;
    }
}
