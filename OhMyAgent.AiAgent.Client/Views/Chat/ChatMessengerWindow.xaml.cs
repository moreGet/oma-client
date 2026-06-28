using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OhMyAgent.AiAgent.Client.Views.Chat;

/// <summary>
/// 실시간 메신저 셸 창(프레임리스, ChatOnlyWindow 크롬 미러).
/// 좌: ChatRoomsView / 우: ChatRoomView 또는 MentionFeedView. DataContext = ChatMessengerViewModel.
/// 수명주기: 닫기=Hide(App.IsExiting 일 때만 실제 Close). StartCommand 는 App 이 최초 표시 시 1회 호출(§6).
/// </summary>
public partial class ChatMessengerWindow : Window
{
    public ChatMessengerWindow(ChatMessengerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Hide-not-Close: 앱 종료가 아니면 창을 숨기고 WS 연결 유지(백그라운드 안읽음 수신).
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
