using System.Windows;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.ViewModels;

namespace OhMyAgent.AiAgent.Client.Views;

/// <summary>
/// 에이전트 런처 창(격자 타일) — 사이드바 "에이전트" 항목이 여는 보조 창.
///
/// 코드비하인드에 로직을 두지 않는다. 닫기는 <c>CloseCommand</c>(타이틀바 X · Esc), 실행은 타일의
/// <c>Launch*</c> 커맨드가 전부 처리하고, 창 수명·Owner 는 <c>AgentLauncherCoordinator</c> 가 쥔다.
/// 남는 것은 커스텀 크롬(WindowStyle=None) 때문에 직접 해야 하는 타이틀바 드래그 하나뿐이다.
/// </summary>
public partial class AgentLauncherWindow : Window
{
    public AgentLauncherWindow(AgentLauncherViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();
}
