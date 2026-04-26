using System.Windows;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OhMyAgent.AiAgent.Client.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

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
