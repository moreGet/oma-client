using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 에이전트 런처 창(앱 아이콘처럼 타일을 격자로 깐 별도 창)의 DataContext.
///
/// 왜 <see cref="AgentSessionViewModel"/> 을 그대로 DataContext 로 쓰지 않는가 —
/// 타일의 계약은 "창을 닫고 나서 실행한다"이고, 그 순서가 세 곳에서 실제로 결과를 바꾼다:
///   1) 파일 첨부: 대화상자는 메인 창이 소유(<c>ShowAttachDialog</c>)한다. 런처가 아직 열려 있으면
///      소유 관계상 런처가 대화상자 위에 그려져 "눌렀는데 아무 것도 안 뜬다"로 보인다.
///   2) /loop · /retry: 두 커맨드는 입력창 프리필 + <c>ComposerFocusRequested</c> 로 포커스를 요구한다.
///      런처가 활성 창인 상태로 실행하면 포커스가 비활성 창(메인)으로 날아가 사용자가 이어 칠 수 없다.
///   3) /help · 새 대화: 결과가 메인 창 전사에 나타나므로 런처가 덮고 있으면 보이지 않는다.
/// 세션 커맨드를 타일에 직접 바인딩하면 이 순서가 사라진다. 그래서 래퍼를 한 겹 두고
/// 타일은 전부 <c>Launch*</c> 만 바인딩한다(5개 커맨드 자체는 세션 VM 것을 재사용한다).
///
/// 개폐 상태 플래그는 두지 않는다 — 런처는 창이므로 <b>창 수명이 곧 상태</b>다.
/// (예전 서랍의 <c>IsAgentDrawerOpen</c> 같은 상태를 되살리면 창 수명과 어긋날 수 있다.)
/// </summary>
public sealed partial class AgentLauncherViewModel : ObservableObject
{
    private readonly AgentSessionViewModel _session;

    /// <summary>
    /// 창을 닫아 달라는 요청. 창은 View 소유라 VM 이 직접 닫을 수 없다.
    /// <c>AgentLauncherCoordinator</c> 가 단독으로 구독한다(창이 구독하면 매 인스턴스마다
    /// 핸들러가 쌓여, 닫힌 창의 핸들러가 남는다).
    /// </summary>
    public event EventHandler? CloseRequested;

    public AgentLauncherViewModel(AgentSessionViewModel session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));

        // 첨부는 실행 중(IsBusy)에 막힌다. 안쪽 커맨드의 CanExecute 변화를 타일까지 옮기지 않으면
        // 타일은 눌리는 것처럼 보이고 창만 닫힌 뒤 아무 일도 일어나지 않는다(무증상 실패).
        // 해제 경로를 두지 않는 근거: 세션 VM 과 이 VM 은 둘 다 App 수명 싱글턴이다(App.xaml.cs 에서 1회 조립).
        _session.RequestAttachFileCommand.CanExecuteChanged +=
            (_, _) => LaunchAttachCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Esc · 타이틀바 닫기 버튼 — 아무 것도 실행하지 않고 창만 닫는다.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>타일 "반복 실행" — 입력창에 "/loop " 를 깔고 포커스를 넘긴다(즉시 시작하지 않는다).</summary>
    [RelayCommand]
    private void LaunchLoop() => Launch(_session.StartLoopCommand);

    /// <summary>타일 "마지막 입력" — 직전에 보낸 메시지를 입력창에 되불러 편집하게 한다.</summary>
    [RelayCommand]
    private void LaunchRetry() => Launch(_session.RetryLastCommand);

    /// <summary>타일 "도움말" — 슬래시 커맨드 안내를 전사에 남긴다.</summary>
    [RelayCommand]
    private void LaunchHelp() => Launch(_session.ShowHelpCommand);

    /// <summary>
    /// 타일 "파일 첨부" — 컴포저 "+" 버튼과 <b>의도적으로 중복</b>된 경로다(자주 쓰는 동작이라 둘 다 둔다).
    /// 세션 VM 이 <c>AttachFileRequested</c> 로 요청하고 메인 창 코드비하인드가 대화상자를 띄우므로,
    /// 런처를 먼저 닫아야 대화상자가 런처 뒤로 숨지 않는다(<see cref="Launch"/> 가 그 순서를 보장).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunchAttach))]
    private void LaunchAttach() => Launch(_session.RequestAttachFileCommand);

    /// <summary>타일 "새 대화" — 현재 세션을 저장하고 새 대화를 시작한다(실행 중 루프도 정지).</summary>
    [RelayCommand]
    private void LaunchNewChat() => Launch(_session.ClearCommand);

    /// <summary>
    /// 타일 "작업 관리" — 실행 중인 턴·도구·자식 프로세스 목록 창을 연다.
    ///
    /// 앞의 5개와 성격이 다르지만(세션 동작이 아니라 <b>자기 창</b>을 여는 항목이다) 같은
    /// <see cref="Launch"/> 래퍼를 쓴다. 근거 둘:
    ///   · "닫고 → 실행" 규약이 그대로 필요하다. 런처가 열린 채면 두 소유 창이 겹쳐 목록 창을 덮고,
    ///     사용자는 "눌렀는데 아무 것도 안 뜬다"로 읽는다.
    ///   · 창을 여는 방법을 세션 VM 한 곳(<c>OpenTaskManagerCommand</c>)에만 두면 App 배선도 한 곳이고,
    ///     배선이 빠졌을 때 전사에 남는 안내도 그 한 곳이 낸다(런처가 판단을 복제하지 않는다).
    /// </summary>
    [RelayCommand]
    private void LaunchTaskManager() => Launch(_session.OpenTaskManagerCommand);

    /// <summary>첨부 가능 여부는 안쪽 커맨드(=!IsBusy)가 정본이다 — 판정을 복제하지 않는다.</summary>
    private bool CanLaunchAttach() => _session.RequestAttachFileCommand.CanExecute(null);

    /// <summary>
    /// 타일 실행의 유일한 경로. 순서가 계약이다 — <b>닫기 → 실행</b>.
    /// 뒤집으면 첨부 대화상자가 런처 뒤에 숨고, 포커스 요청이 비활성 창으로 날아간다(클래스 주석 참조).
    /// </summary>
    private void Launch(ICommand inner)
    {
        // 실행도 못 하는 타일이 창만 닫으면 "눌렀는데 아무 일도 안 났다"로 보인다 — 판정을 먼저 한다.
        // (커맨드로 걸러지므로 UI 에서는 비활성 타일이지만, 코드 경로로도 같은 규칙을 지킨다.)
        if (!inner.CanExecute(null))
            return;

        CloseRequested?.Invoke(this, EventArgs.Empty);
        inner.Execute(null);
    }
}
