using System;
using OhMyAgent.AiAgent.Client.ViewModels;
using OhMyAgent.AiAgent.Client.Views;

namespace OhMyAgent.AiAgent.Client.Services;

public class ChatWindowCoordinator : IChatWindowCoordinator
{
    private readonly Func<MainWindow>             _getMainWindow;
    private readonly Func<MainViewModel>          _getMainVm;
    private readonly ITrayNotificationService     _trayNotification;

    private ChatOnlyWindow? _chatOnlyWindow;

    public ChatWindowCoordinator(
        Func<MainWindow>          getMainWindow,
        Func<MainViewModel>       getMainVm,
        ITrayNotificationService  trayNotification)
    {
        _getMainWindow    = getMainWindow    ?? throw new ArgumentNullException(nameof(getMainWindow));
        _getMainVm        = getMainVm        ?? throw new ArgumentNullException(nameof(getMainVm));
        _trayNotification = trayNotification ?? throw new ArgumentNullException(nameof(trayNotification));
    }

    public void ToggleChatOnly()
    {
        // 메인 창이 보이고 있으면 ChatOnly 토글 무시
        var main = _getMainWindow();
        if (main != null && main.IsVisible)
            return;

        if (_chatOnlyWindow == null)
        {
            _chatOnlyWindow = new ChatOnlyWindow(_getMainVm());
            _chatOnlyWindow.Closed += (_, _) => _chatOnlyWindow = null;
            _chatOnlyWindow.Show();
            _chatOnlyWindow.Activate();
            return;
        }

        if (!_chatOnlyWindow.IsVisible)
        {
            _chatOnlyWindow.Show();
            _chatOnlyWindow.Activate();
        }
        else
        {
            _chatOnlyWindow.Activate();
        }
    }

    public void ShowMain()
    {
        var main = _getMainWindow();
        main.Show();
        main.Activate();
    }
}
