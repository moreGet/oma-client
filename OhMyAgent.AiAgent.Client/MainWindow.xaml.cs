using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using OhMyAgent.AiAgent.Client.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace OhMyAgent.AiAgent.Client;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        vm.Messages.CollectionChanged += Messages_CollectionChanged;
    }

    // Enter → 전송 / Shift+Enter → 줄바꿈
    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) return;

        e.Handled = true;
        if (DataContext is MainViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

    // 새 메시지 도착 시 자동 스크롤
    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ChatScrollViewer.ScrollToEnd();
    }

    // 타이틀바 드래그 이동
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => DragMove();

    // 최소화 → 작업표시줄로
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    // 트레이로 숨기기
    private void TrayButton_Click(object sender, RoutedEventArgs e)
        => ((App)Application.Current).HideToTray(this);

    // X 버튼 → Close() 호출 → OnClosing이 종료 확인 다이얼로그 처리
    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        var result = MessageBox.Show(
            this,
            "프로그램을 종료하시겠습니까?",
            "OhMyAgent",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
        {
            ((App)Application.Current).ExitApplication();
        }
        else
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    // HWND 확보 후 글로벌 핫키 등록
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((App)Application.Current).RegisterMainWindowHwnd(this);
    }
}
