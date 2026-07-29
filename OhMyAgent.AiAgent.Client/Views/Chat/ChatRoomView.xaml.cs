using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace OhMyAgent.AiAgent.Client.Views.Chat;

/// <summary>
/// 한 대화방 View. DataContext = ChatRoomViewModel.
/// code-behind 책임(설계서 §5.3 / 03_viewmodel_summary §UIDesigner):
///  - 무한스크롤: ScrollViewer 상단 근접 시 LoadMoreCommand 실행 + prepend 후 스크롤 위치 보존.
///  - 읽음: 하단 도달 시 MarkReadCommand 실행.
///  - 오토스크롤: 신규 메시지 추가 시 하단 이동(이미 하단 부근일 때만).
///  - Enter 전송 / Shift+Enter 줄바꿈 / 멘션 팝업 활성 시 ↑↓·Enter·Tab·Esc 는 팝업이 먼저 소비.
///  - 첨부: OpenFileDialog 선택 → 경로마다 UploadAndAttachAsync 호출.
///
/// 메시지 ScrollViewer 는 <c>MessagesList</c> 의 ControlTemplate 안에 있다(가상화를 살리려면
/// VirtualizingStackPanel 이 스크롤 주인이어야 한다). 그래서 필드가 아니라 템플릿에서 찾아 캐시한다.
/// </summary>
public partial class ChatRoomView : UserControl
{
    private const double TopLoadThreshold = 80;       // 상단 px 근접 → 더보기
    private const double BottomReadThreshold = 40;     // 하단 px 근접 → 읽음/오토스크롤
    private const double AutoScrollNearBottomPx = 160; // 신규 메시지 시 하단 px 이내면 오토스크롤

    private ChatRoomViewModel? _vm;
    private ScrollViewer? _scroll;

    // 과거 이력 prepend 전후의 스크롤 지표. 위쪽에 콘텐츠가 늘어난 만큼 offset 을 밀어 화면을 고정한다.
    private bool _restorePending;
    private double _extentBeforeLoad;
    private double _offsetBeforeLoad;

    public ChatRoomView()
    {
        InitializeComponent();
        Loaded += (_, _) => ResolveScrollViewer();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>ControlTemplate 안의 ScrollViewer 를 찾아 캐시(템플릿 적용 후에만 유효).</summary>
    private ScrollViewer? ResolveScrollViewer()
    {
        if (_scroll is not null) return _scroll;
        MessagesList.ApplyTemplate();
        _scroll = MessagesList.Template?.FindName("MessagesScroll", MessagesList) as ScrollViewer;
        return _scroll;
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _vm = DataContext as ChatRoomViewModel;
        if (_vm is not null)
        {
            _vm.Messages.CollectionChanged += Messages_CollectionChanged;
            _vm.PropertyChanged += Vm_PropertyChanged;
            _vm.CaretMoveRequested += Vm_CaretMoveRequested;
        }
    }

    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.Messages.CollectionChanged -= Messages_CollectionChanged;
            _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm.CaretMoveRequested -= Vm_CaretMoveRequested;
        }
        _vm = null;
    }

    /// <summary>멘션 확정 후 caret 을 삽입 지점 뒤로(기본 동작대로면 항상 문장 끝으로 튄다).</summary>
    private void Vm_CaretMoveRequested(object? sender, int caretIndex)
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            DraftBox.CaretIndex = Math.Clamp(caretIndex, 0, DraftBox.Text.Length);
            DraftBox.Focus();
        }), DispatcherPriority.Input);

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 최초 메시지 일괄 로드 완료(IsLoading false) 시 한 번만 하단으로(로드 중 add 마다 스크롤하지 않아 깜빡임 방지).
        if (e.PropertyName == nameof(ChatRoomViewModel.IsLoading) && _vm is { IsLoading: false })
            Dispatcher.BeginInvoke(new Action(() => ResolveScrollViewer()?.ScrollToEnd()), DispatcherPriority.Loaded);

        // 과거 이력 로드: 시작 시 지표 기록 → 완료 시 늘어난 높이만큼 offset 을 밀어 보던 위치를 유지.
        if (e.PropertyName != nameof(ChatRoomViewModel.IsLoadingMore) || _vm is null) return;

        if (_vm.IsLoadingMore)
        {
            if (ResolveScrollViewer() is not { } sv) return;
            _extentBeforeLoad = sv.ExtentHeight;
            _offsetBeforeLoad = sv.VerticalOffset;
            _restorePending = true;
        }
        else if (_restorePending)
        {
            _restorePending = false;
            Dispatcher.BeginInvoke(new Action(RestoreScrollAfterPrepend), DispatcherPriority.Loaded);
        }
    }

    /// <summary>prepend 로 위쪽에 늘어난 높이만큼 offset 을 밀어 시야를 고정한다.</summary>
    private void RestoreScrollAfterPrepend()
    {
        if (ResolveScrollViewer() is not { } sv) return;
        sv.UpdateLayout();
        var grown = sv.ExtentHeight - _extentBeforeLoad;
        if (grown <= 0) return;
        sv.ScrollToVerticalOffset(_offsetBeforeLoad + grown);
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (_vm is { IsLoading: true }) return;      // 일괄 로드 중엔 per-add 스크롤 생략(완료 후 1회만)
        if (_vm is { IsLoadingMore: true }) return;  // 과거 이력 prepend 는 오토스크롤 대상이 아니다
        if (ResolveScrollViewer() is not { } sv) return;

        // 실시간 신규 메시지: 사용자가 하단 근처면 오토스크롤(과거 로드 시 위치 유지).
        var nearBottom = sv.ScrollableHeight - sv.VerticalOffset <= AutoScrollNearBottomPx;
        if (nearBottom)
            Dispatcher.BeginInvoke(new Action(() => sv.ScrollToEnd()), DispatcherPriority.Loaded);
    }

    private void MessagesScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null) return;
        _scroll ??= sender as ScrollViewer;
        if (_scroll is not { } sv) return;

        // 상단 근접 → 과거 더 불러오기
        if (e.VerticalChange < 0 && sv.VerticalOffset <= TopLoadThreshold)
        {
            if (_vm.LoadMoreCommand.CanExecute(null))
                _vm.LoadMoreCommand.Execute(null);
        }

        // 하단 도달 → 읽음 처리
        if (sv.ScrollableHeight - sv.VerticalOffset <= BottomReadThreshold)
        {
            if (_vm.MarkReadCommand.CanExecute(null))
                _vm.MarkReadCommand.Execute(null);
        }
    }

    /// <summary>caret 을 아는 쪽은 View 뿐이다 — 문장 중간 @멘션도 정확히 잡도록 위치까지 함께 넘긴다.</summary>
    private void DraftBox_TextChanged(object sender, TextChangedEventArgs e)
        => (DataContext as ChatRoomViewModel)?.NotifyDraftChanged(DraftBox.Text, DraftBox.CaretIndex);

    private void DraftBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ChatRoomViewModel vm) return;

        // 멘션 팝업이 떠 있으면 방향키/확정/취소를 팝업이 먼저 가져간다.
        if (vm.Mentions.IsActive)
        {
            switch (e.Key)
            {
                case Key.Down:
                    vm.Mentions.MoveSelection(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    vm.Mentions.MoveSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    vm.Mentions.Close();
                    e.Handled = true;
                    return;
                case Key.Tab:
                case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    if (vm.Mentions.CommitSelection())
                    {
                        e.Handled = true;
                        return;
                    }
                    break;
            }
        }

        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return; // Shift+Enter = 줄바꿈
        e.Handled = true;
        if (vm.SendCommand.CanExecute(null))
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
