using System.Windows;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.ViewModels;

namespace OhMyAgent.AiAgent.Client.Views;

/// <summary>
/// 작업 관리(태스크 매니저) 창 — 런처 타일과 상단바 "진행 N" 칩이 여는 보조 창.
///
/// 코드비하인드에 로직을 두지 않는다(런처 창과 같은 규약). 닫기는 <c>CloseCommand</c>(타이틀바 X · Esc),
/// 갱신·중지·강제 종료는 VM 커맨드, 창 수명·Owner·구독 해제는 <c>TaskManagerCoordinator</c> 가 쥔다.
/// 남는 것은 커스텀 크롬(WindowStyle=None) 때문에 직접 해야 하는 타이틀바 드래그 하나뿐이다.
/// </summary>
public partial class TaskManagerWindow : Window
{
    public TaskManagerWindow(TaskManagerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();
}
