using System;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>UI 스레드 마샬링 추상화. WPF는 Dispatcher, 헤드리스는 즉시 실행.</summary>
public interface IUiDispatcher
{
    /// <summary>호출 스레드가 이미 UI 스레드인가. 헤드리스 구현은 항상 true.</summary>
    bool CheckAccess();

    /// <summary>UI 스레드에서 실행(이미 UI 스레드면 인라인). 완료까지 관찰 가능한 Task 반환.</summary>
    Task InvokeAsync(Action action);

    /// <summary>UI 스레드에서 동기 실행(블로킹). SettingsService 이벤트 발화용.</summary>
    void Invoke(Action action);
}
