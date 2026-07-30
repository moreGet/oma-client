using System;
using System.Windows;
using OhMyAgent.AiAgent.Client.ViewModels;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 태스크 매니저 창 단일 인스턴스 코디네이터. <see cref="AgentLauncherCoordinator"/> 미러.
///
/// 창 타입(<c>TaskManagerWindow</c>)은 View 단계 산출물이라 컴파일 의존을 피하려
/// <see cref="Window"/> 기반 <see cref="Func{Window}"/> 팩토리를 받는다(형제 코디네이터와 같은 이유).
///
/// 런처와 다른 책임이 하나 있다 — <b>VM 의 구독·타이머 수명을 창 수명에 맞춘다</b>.
/// 등기소·루프 컨트롤러는 App 수명 싱글턴이고 VM 도 1개만 만들어 재사용하므로, 창이 닫힐 때
/// 떼어 주지 않으면 (1) 창을 여닫을수록 구독이 쌓이고 (2) 아무도 보지 않는 화면 때문에
/// 매초 프로세스 관측 syscall 이 계속 돈다.
/// </summary>
public sealed class TaskManagerCoordinator : ITaskManagerCoordinator
{
    private readonly TaskManagerViewModel _viewModel;
    private readonly Func<Window>         _windowFactory;
    private readonly Func<Window?>        _ownerProvider;

    private Window? _window;

    public TaskManagerCoordinator(
        TaskManagerViewModel viewModel,
        Func<Window>         windowFactory,
        Func<Window?>        ownerProvider)
    {
        _viewModel     = viewModel      ?? throw new ArgumentNullException(nameof(viewModel));
        _windowFactory = windowFactory  ?? throw new ArgumentNullException(nameof(windowFactory));
        _ownerProvider = ownerProvider  ?? throw new ArgumentNullException(nameof(ownerProvider));

        // 닫기 요청(Esc · 타이틀바 X)은 창이 아니라 여기서 받는다 — VM 은 App 수명 싱글턴이고
        // 창은 매번 새로 만들어지므로, 창이 구독하면 닫힌 창의 핸들러가 계속 쌓인다.
        _viewModel.CloseRequested += (_, _) => Close();
    }

    public bool IsOpen => _window is { IsVisible: true };

    public void Show()
    {
        if (_window is null)
        {
            _window = _windowFactory();
            _window.Closed += (_, _) =>
            {
                _window = null;             // 캐시를 비워야 다음 열기에 새로 만든다(닫힌 창은 Show 못 함).
                _viewModel.Detach();        // 타이머·구독 해제 — 누수와 헛도는 관측을 여기서 끊는다.
            };

            // Owner 는 Show 전에 잡아야 WindowStartupLocation=CenterOwner 가 먹는다.
            // 소유 창은 소유자보다 항상 위에 그려지므로 메인 창이 Topmost 여도 이 창이 그 아래로 숨지 않는다
            // (그래서 Topmost 를 따로 걸지 않는다 — 걸면 다른 앱 위로도 떠 버린다).
            _window.Owner = _ownerProvider();

            // 창이 보이기 직전에 붙인다 — 첫 프레임이 빈 목록으로 보이지 않게 Attach 가 즉시 1회 갱신한다.
            _viewModel.Attach();
            _window.Show();
            _window.Activate();
            return;
        }

        // 이미 열려 있으면 새 창을 또 띄우지 않고 기존 창을 올린다(타일·칩 연타 대비).
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Close() => _window?.Close();
}
