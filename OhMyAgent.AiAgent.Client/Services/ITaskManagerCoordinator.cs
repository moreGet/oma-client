namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 태스크 매니저 창의 표시/닫기 단일 진입점. <see cref="IAgentLauncherCoordinator"/> 미러 —
/// 형제 코디네이터들과 같은 모양을 유지해 창 수명 관리 코드를 찾는 사람이 한 곳만 보면 되게 한다.
///
/// 런처와 다른 점은 하나다: 이 창은 <b>실행을 지켜보는 창</b>이라 항목을 조작해도 닫히지 않는다.
/// 대신 창이 닫힐 때 VM 의 1초 타이머·등기소 구독을 반드시 끊는다(구현 주석 참조).
/// </summary>
public interface ITaskManagerCoordinator
{
    /// <summary>태스크 매니저 창이 지금 보이고 있는가.</summary>
    bool IsOpen { get; }

    /// <summary>창을 띄운다. 이미 열려 있으면 새 창을 만들지 않고 기존 창을 활성화한다.</summary>
    void Show();

    /// <summary>열려 있으면 닫는다(Esc · 타이틀바 X · 외부 정리 경로).</summary>
    void Close();
}
