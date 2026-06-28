namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// ChatMessengerWindow 토글/표시/숨김(설계서 §3.4). ChatWindowCoordinator 미러 — 창 lazy 생성·재사용, Hide-not-Close.
/// </summary>
public interface IChatMessengerCoordinator
{
    void Toggle();          // 창 토글(핫키/트레이/버튼)
    void Show();
    void HideToTray();
    bool IsOpen { get; }
}
