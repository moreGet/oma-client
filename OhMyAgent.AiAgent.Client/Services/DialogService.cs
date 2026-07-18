using System.Linq;
using System.Windows;
using OhMyAgent.AiAgent.Client.Views.Chat;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 코드비하인드에 흩어져 있던 알림(MessageBox)·텍스트 입력 다이얼로그를 한곳으로 모은 구현.
/// 입력 프롬프트는 기존 다크 테마 입력 모달(<see cref="ChatInputDialog"/>)을 재사용해
/// 앱의 시각 언어(WindowBg/DarkTextBox/PrimaryButton/OutlineButton 토큰)를 그대로 유지한다.
/// </summary>
public sealed class DialogService : IDialogService
{
    public void ShowMessage(string title, string message, DialogSeverity severity = DialogSeverity.Information)
    {
        var image = severity switch
        {
            DialogSeverity.Warning => MessageBoxImage.Warning,
            DialogSeverity.Error   => MessageBoxImage.Error,
            _                      => MessageBoxImage.Information,
        };

        var owner = ActiveOwner();
        if (owner is not null)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
    }

    public string? PromptText(string title, string message, string defaultValue = "", bool multiline = false)
        => ChatInputDialog.Prompt(ActiveOwner(), title, message, defaultValue, multiline);

    /// <summary>현재 활성(포커스) 창 → 없으면 MainWindow. 모달 소유·센터링 기준.</summary>
    private static Window? ActiveOwner()
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
               ?? app.MainWindow;
    }
}
