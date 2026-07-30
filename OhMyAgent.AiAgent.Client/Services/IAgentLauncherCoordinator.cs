namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 에이전트 런처 창의 표시/닫기 단일 진입점. <see cref="IChatMessengerCoordinator"/> 미러.
/// 인터페이스를 두는 이유는 형제 코디네이터 두 개(<see cref="IChatWindowCoordinator"/>,
/// <see cref="IChatMessengerCoordinator"/>)와 같은 모양을 유지해, 창 수명 관리 코드를
/// 찾는 사람이 한 곳만 보면 되게 하는 것이다.
/// </summary>
public interface IAgentLauncherCoordinator
{
    /// <summary>런처 창이 지금 보이고 있는가.</summary>
    bool IsOpen { get; }

    /// <summary>런처를 띄운다. 이미 열려 있으면 새 창을 만들지 않고 기존 창을 활성화한다.</summary>
    void Show();

    /// <summary>열려 있으면 닫는다(타일 실행 · 외부 정리 경로에서 호출).</summary>
    void Close();
}
