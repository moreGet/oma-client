using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace OhMyAgent.AiAgent.Client.Views.Chat;

/// <summary>
/// 한 대화방 View. DataContext = ChatRoomViewModel.
/// code-behind 책임(설계서 §5.3 / 03_viewmodel_summary §UIDesigner):
///  - 무한스크롤: ScrollViewer 상단 근접 시 LoadMoreCommand 실행.
///  - 읽음: 하단 도달 시 MarkReadCommand 실행.
///  - 오토스크롤: 신규 메시지 추가 시 하단 이동(이미 하단 부근일 때만).
///  - Enter 전송 / Shift+Enter 줄바꿈.
///  - 첨부: OpenFileDialog 선택 → 경로마다 UploadAndAttachAsync 호출.
/// </summary>
public partial class ChatRoomView : UserControl
{
    private const double TopLoadThreshold = 80;       // 상단 px 근접 → 더보기
    private const double BottomReadThreshold = 40;     // 하단 px 근접 → 읽음/오토스크롤
    private const double AutoScrollNearBottomPx = 160; // 신규 메시지 시 하단 px 이내면 오토스크롤
    private ChatRoomViewModel? _vm;

    public ChatRoomView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachMessages();
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        DetachMessages();
        _vm = DataContext as ChatRoomViewModel;
        if (_vm is not null)
            _vm.Messages.CollectionChanged += Messages_CollectionChanged;
    }

    private void DetachMessages()
    {
        if (_vm is not null)
            _vm.Messages.CollectionChanged -= Messages_CollectionChanged;
        _vm = null;
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 신규 메시지 추가 시, 사용자가 하단 근처면 오토스크롤(과거 로드 시 위치 유지).
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        bool nearBottom = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset <= AutoScrollNearBottomPx;
        if (nearBottom)
            Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToEnd()));
    }

    private void MessagesScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null) return;

        // 상단 근접 → 과거 더 불러오기
        if (e.VerticalChange < 0 && MessagesScroll.VerticalOffset <= TopLoadThreshold)
        {
            if (_vm.LoadMoreCommand.CanExecute(null))
                _vm.LoadMoreCommand.Execute(null);
        }

        // 하단 도달 → 읽음 처리
        if (MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset <= BottomReadThreshold)
        {
            if (_vm.MarkReadCommand.CanExecute(null))
                _vm.MarkReadCommand.Execute(null);
        }
    }

    private void DraftBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return; // Shift+Enter = 줄바꿈
        e.Handled = true;
        if (DataContext is ChatRoomViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatRoomViewModel vm) return;

        var dialog = new OpenFileDialog { Multiselect = true, Title = "첨부할 파일 선택" };
        if (dialog.ShowDialog() != true) return;

        foreach (var path in dialog.FileNames)
        {
            try { await vm.UploadAndAttachAsync(path); }
            catch { /* 업로드 실패는 VM의 StatusMessage 로 안내됨 */ }
        }
    }
}
