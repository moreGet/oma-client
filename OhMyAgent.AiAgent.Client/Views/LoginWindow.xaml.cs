using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // ID 에서 Enter → 비밀번호 칸을 펼치고 포커스 이동.
    private void UsernameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Proceed();
    }

    // "다음" 버튼 — Enter 와 동일 동작.
    private void NextButton_Click(object sender, RoutedEventArgs e)
        => Proceed();

    /// <summary>ID 입력을 확정하고 비밀번호 단계로 넘어간다(Enter / "다음" 공통).</summary>
    private void Proceed()
    {
        if (string.IsNullOrWhiteSpace(_vm.Username)) return;

        RevealPasswordSection();
        PasswordBox.Focus();
    }

    // 비밀번호에서 Enter → 로그인 실행.
    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (_vm.LoginCommand.CanExecute(null))
            _vm.LoginCommand.Execute(null);
    }

    private bool _passwordRevealed;

    private void RevealPasswordSection()
    {
        if (_passwordRevealed) return;
        _passwordRevealed = true;

        // "다음" → "로그인" 으로 교체.
        NextButton.Visibility = Visibility.Collapsed;
        LoginButton.Visibility = Visibility.Visible;

        // 접힌 상태(Height=0)라 자연 높이를 알 수 없다 — 잠시 Auto 로 두고 측정.
        PasswordSection.Height = double.NaN;
        PasswordSection.Measure(new System.Windows.Size(PasswordSection.ActualWidth, double.PositiveInfinity));
        var target = PasswordSection.DesiredSize.Height;
        PasswordSection.Height = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);

        var grow = new DoubleAnimation(0, target, duration) { EasingFunction = ease };
        grow.Completed += (_, _) =>
        {
            // 애니메이션 해제 후 Auto 로 — 폰트/DPI 변화에도 높이가 고정되지 않도록.
            PasswordSection.BeginAnimation(HeightProperty, null);
            PasswordSection.Height = double.NaN;
            PasswordSection.ClipToBounds = false;
        };

        PasswordSection.BeginAnimation(HeightProperty, grow);
        PasswordSection.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        PasswordSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-10, 0, duration) { EasingFunction = ease });
    }
}
