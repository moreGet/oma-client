using System;
using System.Windows.Forms;

namespace OhMyAgent.AiAgent.Client.Services;

public class TrayNotificationService : ITrayNotificationService
{
    private const string AppTitle = "OhMyAgent";

    private readonly NotifyIcon _notifyIcon;

    public TrayNotificationService(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon ?? throw new ArgumentNullException(nameof(notifyIcon));
    }

    public void ShowHideHint(string hotkeyDisplayText)
    {
        _notifyIcon.ShowBalloonTip(
            3000,
            AppTitle,
            $"{hotkeyDisplayText}를 누르면 채팅창이 열립니다",
            ToolTipIcon.Info);
    }

    public void ShowInfo(string title, string text, int timeoutMs = 3000)
    {
        _notifyIcon.ShowBalloonTip(timeoutMs, title, text, ToolTipIcon.Info);
    }
}
