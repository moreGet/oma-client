using System;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// WPF <c>Dispatcher</c> 기반 <see cref="IUiDispatcher"/> 구현. 기존 static <c>UiDispatch</c> 로직을
/// 1:1 이식한다. UI 스레드가 없거나(디자인 타임/부트 이전) 이미 UI 스레드면 인라인 실행.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public bool CheckAccess()
        => System.Windows.Application.Current?.Dispatcher?.CheckAccess() ?? true;

    public Task InvokeAsync(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) { action(); return Task.CompletedTask; }
        return d.InvokeAsync(action).Task;
    }

    public void Invoke(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.Invoke(action);
    }
}
