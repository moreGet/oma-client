using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.ViewModels;
using OhMyAgent.AiAgent.Client.ViewModels.Transcript;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OhMyAgent.AiAgent.Client.Views;

public partial class ChatOnlyWindow : Window
{
    private bool _autoScroll = true;

    public ChatOnlyWindow(AgentSessionViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Transcript.CollectionChanged += Transcript_CollectionChanged;
        ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;
        Closed += (_, _) => vm.Transcript.CollectionChanged -= Transcript_CollectionChanged;
    }

    // 스트리밍 추종 + 스크롤 잠금(MainWindow 와 동일 규칙).
    private void Transcript_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (e.NewItems?.Count > 0 && e.NewItems[0] is UserTurnViewModel)
            _autoScroll = true;
        if (_autoScroll)
            ChatScrollViewer.ScrollToEnd();
    }

    private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0)
        {
            if (_autoScroll) ChatScrollViewer.ScrollToEnd();
        }
        else if (e.VerticalChange != 0)
        {
            _autoScroll = ChatScrollViewer.ScrollableHeight - ChatScrollViewer.VerticalOffset < 12;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Hide();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (DataContext is AgentSessionViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

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
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
