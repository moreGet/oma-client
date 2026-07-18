using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OhMyAgent.AiAgent.Client.ViewModels;
using OhMyAgent.AiAgent.Client.ViewModels.Transcript;
using OhMyAgent.AiAgent.Client.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace OhMyAgent.AiAgent.Client;

public partial class MainWindow : Window
{
    // 자동 스크롤 추종 여부. 사용자가 위로 스크롤하면 꺼지고, 바닥으로 돌아오거나 새 메시지를 보내면 켜진다.
    private bool _autoScroll = true;

    public MainWindow(AgentSessionViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        vm.Transcript.CollectionChanged += Transcript_CollectionChanged;
        ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;
    }

    // @파일 자동완성: 텍스트/caret 변화 → 후보 갱신.
    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is AgentSessionViewModel vm)
            vm.FileMention.UpdateFromInput(InputBox.Text, InputBox.CaretIndex);
    }

    private void InputBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // caret 만 움직여도(텍스트 변화 없이) 토큰 문맥이 바뀔 수 있다.
        if (DataContext is AgentSessionViewModel vm)
            vm.FileMention.UpdateFromInput(InputBox.Text, InputBox.CaretIndex);
    }

    // 후보 클릭 → 삽입.
    private void MentionList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AcceptMention();

    // 선택된 후보를 입력창에 삽입(토큰을 "@경로 "로 대체하고 caret 이동).
    private void AcceptMention()
    {
        if (DataContext is not AgentSessionViewModel vm) return;
        var applied = vm.FileMention.Accept(InputBox.Text);
        if (applied is not { } r) return;

        InputBox.Text = r.Text;          // 양방향 바인딩으로 InputText 도 갱신.
        InputBox.CaretIndex = r.Caret;
        InputBox.Focus();
    }

    // Enter → 전송 / Shift+Enter → 줄바꿈 / Esc → 실행 중이면 중지 / 빈 입력에서 ↑ → 마지막 메시지 되불러오기
    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not AgentSessionViewModel vm) return;

        // @자동완성이 열려 있으면 방향/확정/취소 키를 팝업이 먼저 가져간다.
        if (vm.FileMention.IsActive)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.Down:
                    e.Handled = true; vm.FileMention.MoveSelection(+1); return;
                case System.Windows.Input.Key.Up:
                    e.Handled = true; vm.FileMention.MoveSelection(-1); return;
                case System.Windows.Input.Key.Enter:
                case System.Windows.Input.Key.Tab:
                    e.Handled = true; AcceptMention(); return;
                case System.Windows.Input.Key.Escape:
                    e.Handled = true; vm.FileMention.Close(); return;
            }
        }

        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter
                when !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift):
                e.Handled = true;
                if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
                break;

            case System.Windows.Input.Key.Escape when vm.StopCommand.CanExecute(null):
                // 실행 중일 때만 중지. 그 외엔 기본 동작(포커스 등)을 막지 않는다.
                e.Handled = true;
                vm.StopCommand.Execute(null);
                break;

            case System.Windows.Input.Key.Up
                when string.IsNullOrEmpty(vm.InputText) && !string.IsNullOrEmpty(vm.LastSentMessage):
                // 셸 히스토리처럼 — 빈 입력창에서 위 화살표는 마지막 메시지를 되불러 편집.
                e.Handled = true;
                vm.InputText = vm.LastSentMessage;
                break;
        }
    }

    // 새 항목 도착 시 — 사용자가 방금 보낸 메시지면 추종을 다시 켜고 바닥으로. 그 외엔 추종 중일 때만.
    private void Transcript_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        if (e.NewItems?.Count > 0 && e.NewItems[0] is UserTurnViewModel)
            _autoScroll = true;   // 사용자가 보낸 새 턴 → 항상 따라간다(위로 봤더라도 재개).

        if (_autoScroll)
            ChatScrollViewer.ScrollToEnd();
    }

    // 스트리밍으로 콘텐츠가 자라거나 사용자가 직접 스크롤할 때 — 추종/잠금을 갱신한다.
    // CollectionChanged 는 항목 "추가"만 잡으므로, 스트리밍 중 텍스트가 늘어나는(ExtentHeight 증가) 경우는 여기서 처리.
    private void ChatScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0)
        {
            // 콘텐츠가 자랐다(스트리밍/새 블록) — 추종 중이면 바닥 유지.
            if (_autoScroll)
                ChatScrollViewer.ScrollToEnd();
        }
        else if (e.VerticalChange != 0)
        {
            // 사용자가 직접 스크롤 — 바닥 근처면 추종 재개, 위로 올렸으면 잠근다(스트리밍 중 강제로 끌어내리지 않게).
            _autoScroll = ChatScrollViewer.ScrollableHeight - ChatScrollViewer.VerticalOffset < 12;
        }
    }

    // + 버튼 → 파일 첨부 다이얼로그 (MVVM 안전: 선택 경로를 VM 진입점으로 전달)
    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AgentSessionViewModel vm) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "첨부할 파일 선택",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            foreach (var path in dialog.FileNames)
                vm.AddAttachmentPublic(path);
        }
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

    // HWND 확보 후 글로벌 핫키 등록 + 메신저 안읽음 배지 중계 연결
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var app = (App)Application.Current;
        app.RegisterMainWindowHwnd(this);
        HookMessengerUnread(app);
    }

    // ── 실시간 메신저 진입점 + 안읽음 배지 중계 ──────────────────────────

    /// <summary>사이드바 "메신저" 버튼 → App.ToggleMessenger()(최초 표시 시 Start 가드 포함).</summary>
    private void MenuItem_Messenger_Click(object sender, RoutedEventArgs e)
        => ((App)Application.Current).ToggleMessenger();

    /// <summary>사이드바 안읽음 Pill 배지(메신저 VM.TotalUnread 중계). best-effort.</summary>
    public static readonly DependencyProperty MessengerUnreadProperty =
        DependencyProperty.Register(nameof(MessengerUnread), typeof(int), typeof(MainWindow),
            new PropertyMetadata(0));

    public int MessengerUnread
    {
        get => (int)GetValue(MessengerUnreadProperty);
        set => SetValue(MessengerUnreadProperty, value);
    }

    /// <summary>메신저 VM 의 TotalUnread 변화를 MainWindow 의 MessengerUnread 로 중계(있을 때만).</summary>
    private void HookMessengerUnread(App app)
    {
        var vm = app.ChatMessengerVm;
        if (vm is null) return;

        void Relay() => MessengerUnread = vm.TotalUnread;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModels.Chat.ChatMessengerViewModel.TotalUnread))
                Dispatcher.BeginInvoke(new Action(Relay));
        };
        Relay();   // 초기값 동기화
    }

    private void MenuItem_HotkeySettings_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var settings = app.SettingsService;
        var api = app.Api;
        if (settings == null || api == null) return;

        var settingsVm = new SettingsViewModel(settings, api);
        settingsVm.LoggedOut += (_, _) => ((App)Application.Current).ReturnToLogin();
        _ = settingsVm.InitializeAsync();
        var settingsWindow = new SettingsWindow(settingsVm) { Owner = this };
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    // ── 프로젝트 사이드바 (Projects ViewModel 경유) ───────────────────────

    private ProjectsViewModel? ProjectsVm
        => (DataContext as AgentSessionViewModel)?.Projects;

    // 새 프로젝트 → 이름 입력 후 생성
    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsVm is not { } projects) return;

        var name = ((App)Application.Current).Dialogs.PromptText("새 프로젝트", "프로젝트 이름을 입력하세요:", "새 프로젝트");
        if (name is null) return; // 취소

        projects.CreateProjectCommand.Execute(name);
    }

    // 프로젝트 이름 변경 (컨텍스트 메뉴) — 대상 프로젝트를 먼저 선택
    private void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsVm is not { } projects) return;
        if (((FrameworkElement)sender).DataContext is not ProjectItemViewModel item || item.IsUnassigned) return;

        projects.SelectedProject = item;
        var name = ((App)Application.Current).Dialogs.PromptText("이름 변경", "새 프로젝트 이름:", item.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        projects.RenameCommand.Execute(name);
    }

    // 프로젝트 삭제 (컨텍스트 메뉴) — 귀속 대화 유지 여부 확인
    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsVm is not { } projects) return;
        if (((FrameworkElement)sender).DataContext is not ProjectItemViewModel item || item.IsUnassigned) return;

        projects.SelectedProject = item;

        var result = MessageBox.Show(
            this,
            $"'{item.Name}' 프로젝트를 삭제하시겠습니까?\n\n[예] 귀속 대화도 함께 삭제\n[아니오] 대화는 미분류로 이동\n[취소] 중단",
            "프로젝트 삭제",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Cancel) return;

        projects.DeleteCommand.Execute(result == MessageBoxResult.Yes);
    }

    // 프로젝트 서버 동기화 (컨텍스트 메뉴)
    private void SyncProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsVm is not { } projects) return;
        if (((FrameworkElement)sender).DataContext is not ProjectItemViewModel item || item.IsUnassigned) return;

        projects.SelectedProject = item;
        projects.SyncCommand.Execute(null);
    }

    // 대화 → 프로젝트 편입/해제 (컨텍스트 메뉴 항목).
    //   MenuItem.Tag = 대상 ProjectId(미분류=UnassignedId), 대화는 ContextMenu.PlacementTarget의 DataContext.
    private void AssignConversation_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectsVm is not { } projects) return;
        if (sender is not System.Windows.Controls.MenuItem menuItem) return;
        if (menuItem.Tag is not string targetProjectId) return;

        // PlacementTarget(대화 행 Grid)의 DataContext = ChatSessionSummary
        var contextMenu = menuItem.Parent as System.Windows.Controls.ContextMenu
                          ?? (menuItem.Parent as FrameworkElement)?.TemplatedParent as System.Windows.Controls.ContextMenu;
        var session = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as Models.ChatSessionSummary;
        if (session is null) return;

        projects.AssignConversationCommand.Execute((session.Id, targetProjectId));
    }

    // ── 대화 → 프로젝트 폴더 드래그앤드롭 분류 ─────────────────────────────
    private System.Windows.Point _dragStartPoint;
    private Models.ChatSessionSummary? _dragSession;

    private void Conversation_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragSession = (sender as FrameworkElement)?.DataContext as Models.ChatSessionSummary;
    }

    private void Conversation_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragSession is null) return;

        var pos = e.GetPosition(null);
        if (System.Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var session = _dragSession;
        _dragSession = null;
        var data = new System.Windows.DataObject(typeof(Models.ChatSessionSummary), session);
        System.Windows.DragDrop.DoDragDrop((DependencyObject)sender, data, System.Windows.DragDropEffects.Move);
    }

    private void Project_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Models.ChatSessionSummary))
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Project_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (ProjectsVm is not { } projects) return;
        if (e.Data.GetData(typeof(Models.ChatSessionSummary)) is not Models.ChatSessionSummary session) return;
        if ((sender as FrameworkElement)?.DataContext is not ProjectItemViewModel target) return;

        // 대상 프로젝트(또는 미분류 그룹)로 편입/해제. ProjectsViewModel이 LoadAsync로 갱신.
        projects.AssignConversationCommand.Execute((session.Id, target.Id));
        e.Handled = true;
    }
}
