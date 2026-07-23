using System;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 헤드리스/서버/테스트용 <see cref="IUiDispatcher"/> 구현. 마샬 대상 UI 스레드가 없으므로
/// 모든 호출을 호출 스레드에서 즉시 실행한다. <see cref="CheckAccess"/> 는 항상 true.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public void Invoke(Action action) => action();
}
