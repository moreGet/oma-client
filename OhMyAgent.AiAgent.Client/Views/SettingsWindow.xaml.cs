using System.Windows;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OhMyAgent.AiAgent.Client.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // PasswordBox cannot bind; seed it from the VM once.
        AuthTokenBox.Password = vm.AuthToken;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    // Folder picker → persists via SetWorkspaceRootAsync.
    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "작업 디렉토리를 선택하세요",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (!string.IsNullOrWhiteSpace(_vm.WorkspaceRoot))
            dialog.SelectedPath = _vm.WorkspaceRoot;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _ = _vm.SetWorkspaceRootAsync(dialog.SelectedPath);
    }

    // PasswordBox is not bindable — push the value into the VM on change.
    private void AuthTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.AuthToken = AuthTokenBox.Password;
    }

    // Login PasswordBox is not bindable — push the value into the VM on change.
    private void LoginPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.Password = LoginPasswordBox.Password;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            // Alt 조합 시 SystemKey 우선
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            vm.ApplyCapturedKey(key, Keyboard.Modifiers);
        }
        base.OnKeyDown(e);
    }
}
