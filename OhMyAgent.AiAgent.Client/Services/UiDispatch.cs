using System;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// WPF UI 스레드 마샬링 공용 헬퍼. 오케스트레이터 스트림·백그라운드 서비스가 UI 밖에서 도는 동안
/// Transcript/속성 변경을 UI 스레드로 올린다. 이미 UI 스레드면 즉시 실행(불필요한 디스패치 회피).
/// </summary>
internal static class UiDispatch
{
    public static Task InvokeAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
