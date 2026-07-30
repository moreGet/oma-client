namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>알림 대화상자의 의미(아이콘) 구분 — 기존 MessageBoxImage 사용처와 1:1 대응.</summary>
public enum DialogSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// 코드비하인드의 직접 다이얼로그 처리(MessageBox / 절차적 입력 창)를 대체하는 UI 대화상자 서비스.
/// owner 창은 구현이 활성 창(또는 MainWindow)으로 결정한다 — 호출부는 소유 창을 몰라도 된다.
/// </summary>
public interface IDialogService
{
    /// <summary>제목/본문 알림. 기본 심각도는 정보. UI 스레드에서 호출한다.</summary>
    void ShowMessage(string title, string message, DialogSeverity severity = DialogSeverity.Information);

    /// <summary>텍스트 입력 프롬프트. 확인 시 입력값(빈 문자열 허용), 취소 시 <c>null</c>.</summary>
    string? PromptText(string title, string message, string defaultValue = "", bool multiline = false);

    /// <summary>
    /// 되돌릴 수 없는 동작의 확인. 진행=true, 취소=false.
    /// 기본 선택은 <b>취소</b>여야 한다 — 목록 화면에서 Enter 를 습관적으로 누르다 프로세스를
    /// 강제 종료하는 사고를 막는 장치다(태스크 매니저의 전역 중지·프로세스 정리·강제 종료가 쓴다).
    /// </summary>
    bool Confirm(string title, string message, DialogSeverity severity = DialogSeverity.Warning);
}
