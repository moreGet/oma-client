using System.Windows;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OhMyAgent.AiAgent.Client.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) =>
        {
            UsernameBox.Focus();
            _vm.RefreshStatusCommand.Execute(null);   // 서버 상태 자동 확인
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    // PasswordBox 는 바인딩 불가 — 변경 시 VM 으로 푸시.
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _vm.Password = PasswordBox.Password;

    // Enter 로 로그인 실행.
    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.LoginCommand.CanExecute(null))
            _vm.LoginCommand.Execute(null);
    }
}
