using System;
using System.Windows;
using OhMyAgent.AiAgent.Client.ViewModels;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 에이전트 런처 창 단일 인스턴스 코디네이터. <c>ChatMessengerCoordinator</c> 미러.
///
/// 창 타입(<c>AgentLauncherWindow</c>)은 View 단계 산출물이라, 컴파일 의존을 피하려
/// <see cref="Window"/> 기반 <see cref="Func{Window}"/> 팩토리를 받는다(형제 코디네이터와 같은 이유).
///
/// 메신저와 달리 <b>Hide 가 아니라 실제 Close</b> 다 — 런처는 항목을 고르면 사라지는 일회성 표면이고
/// 안에 보존할 상태(스크롤·선택·입력)가 없다. 매번 새로 만들면 위치가 항상 <c>CenterOwner</c> 로
/// 되돌아가 "런처는 늘 같은 자리에 뜬다"가 성립한다(시작 메뉴와 같은 기대).
/// </summary>
public sealed class AgentLauncherCoordinator : IAgentLauncherCoordinator
{
    private readonly Func<Window>  _windowFactory;
    private readonly Func<Window?> _ownerProvider;

    private Window? _window;

    public AgentLauncherCoordinator(
        AgentLauncherViewModel viewModel,
        Func<Window>           windowFactory,
        Func<Window?>          ownerProvider)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
        _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));

        // 타일 실행 → 창 닫기. 구독을 창이 아니라 여기서 하는 이유: VM 은 App 수명 싱글턴이고 창은
        // 매번 새로 만들어지므로, 창이 구독하면 닫힌 창의 핸들러가 계속 쌓인다(누수 + 이미 닫힌 창에 Close 재호출).
        viewModel.CloseRequested += (_, _) => Close();
    }

    public bool IsOpen => _window is { IsVisible: true };

    public void Show()
    {
        if (_window is null)
        {
            _window = _windowFactory();
            _window.Closed += (_, _) => _window = null;   // 캐시를 비워야 다음 열기에 새로 만든다(닫힌 창은 Show 못 함).

            // Owner 는 Show 전에 잡아야 WindowStartupLocation=CenterOwner 가 먹는다.
            // 소유 창은 소유자보다 항상 위에 그려지므로, 메인 창이 Topmost 여도 런처가 그 아래로 숨지 않는다
            // (그래서 런처에 Topmost 를 따로 걸지 않는다 — 걸면 다른 앱 위로도 떠 버린다).
            _window.Owner = _ownerProvider();
            _window.Show();
            _window.Activate();
            return;
        }

        // 이미 열려 있으면 새 창을 또 띄우지 않고 기존 창을 올린다(사이드바 항목 연타 대비).
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Close() => _window?.Close();
}
