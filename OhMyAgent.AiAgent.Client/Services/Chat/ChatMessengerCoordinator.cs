using System;
using System.ComponentModel;
using System.Windows;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// ChatMessengerWindow 토글 코디네이터(설계서 §3.4). ChatWindowCoordinator 미러 — 창 lazy 생성·재사용, Hide-not-Close.
/// ChatMessengerWindow 타입은 UI 단계에서 생성되므로, 컴파일 의존을 피하려 <see cref="Window"/> 기반 <see cref="Func{Window}"/> 팩토리를 받는다.
/// App.IsExiting 일 때만 실제 Close, 그 외 닫기 시도는 Hide 로 가로챈다.
/// </summary>
public sealed class ChatMessengerCoordinator(
    Func<Window> windowFactory,
    ITrayNotificationService tray) : IChatMessengerCoordinator
{
    private readonly Func<Window> _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
    private readonly ITrayNotificationService _tray = tray ?? throw new ArgumentNullException(nameof(tray));

    private Window? _window;

    public bool IsOpen => _window is { IsVisible: true };

    public void Toggle()
    {
        if (IsOpen) HideToTray();
        else Show();
    }

    public void Show()
    {
        EnsureWindow();
        if (!_window!.IsVisible)
            _window.Show();
        _window.Activate();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
    }

    public void HideToTray()
    {
        if (_window is { IsVisible: true })
            _window.Hide();
    }

    private void EnsureWindow()
    {
        if (_window is not null)
            return;

        _window = _windowFactory();
        // Hide-not-Close: App 종료 중이 아니면 닫기 요청을 Hide 로 가로챈다.
        _window.Closing += OnWindowClosing;
        _window.Closed  += (_, _) => _window = null;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // App.IsExiting 은 정적 프로퍼티(App.xaml.cs). 종료 중이 아니면 닫기 취소 후 트레이로 숨김.
        if (!IsAppExiting())
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private static bool IsAppExiting()
        => System.Windows.Application.Current is App && App.IsExiting;
}
