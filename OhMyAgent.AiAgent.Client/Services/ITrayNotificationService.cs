namespace OhMyAgent.AiAgent.Client.Services;

public interface ITrayNotificationService
{
    void ShowHideHint(string hotkeyDisplayText);
    void ShowInfo(string title, string text, int timeoutMs = 3000);
}
