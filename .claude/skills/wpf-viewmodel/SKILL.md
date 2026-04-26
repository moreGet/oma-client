---
name: wpf-viewmodel
description: >
  WPF MVVM ViewModel 구현 스킬. INotifyPropertyChanged, RelayCommand, AsyncRelayCommand,
  ObservableCollection, CommunityToolkit.Mvvm 소스 생성기 활용.
  wpf-orchestrator 내 ViewModelEngineer 에이전트가 사용.
---

# WPF ViewModel 구현 가이드

## CommunityToolkit.Mvvm 사용 (패키지 있을 때)

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

public partial class ChatViewModel(IChatService chatService) : ObservableObject
{
    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<AgentMessage> Messages { get; } = [];

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var response = await chatService.SendMessageAsync(InputText, ct);
            Messages.Add(response);
            InputText = string.Empty;
        }
        catch (AgentException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(InputText) && !IsBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;
}
```

## CommunityToolkit 없을 때 — 직접 구현

```csharp
public class ChatViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _inputText = string.Empty;
    public string InputText
    {
        get => _inputText;
        set { _inputText = value; OnPropertyChanged(); SendCommand.RaiseCanExecuteChanged(); }
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

## RelayCommand 직접 구현 (패키지 없을 때)

```csharp
public class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? _) => canExecute?.Invoke() ?? true;
    public void Execute(object? _) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
```

## MVVM 절대 금지 사항

- ViewModel에서 `MessageBox.Show()` 직접 호출 — 대신 `DialogService` 인터페이스 사용
- ViewModel에서 `Window`, `Button` 등 UIElement 참조
- ViewModel에서 `Application.Current.Dispatcher` 직접 접근 — `SynchronizationContext` 사용
- `static` 필드로 서비스 의존성 보유 — 항상 생성자 주입

## UI 스레드 안전한 컬렉션 업데이트

```csharp
// ObservableCollection은 생성된 스레드에서만 수정 가능
// 백그라운드 스레드에서 추가할 때:
await _dispatcher.InvokeAsync(() => Messages.Add(message));
// 또는 App.xaml.cs에서 BindingOperations.EnableCollectionSynchronization 설정
```

## IDisposable 패턴

이벤트 구독이 있으면 반드시 해제한다:
```csharp
public sealed class ChatViewModel : ObservableObject, IDisposable
{
    public ChatViewModel(IEventBus bus)
    {
        bus.MessageReceived += OnMessageReceived;
        _unsubscribe = () => bus.MessageReceived -= OnMessageReceived;
    }
    private readonly Action _unsubscribe;
    public void Dispose() => _unsubscribe();
}
```

## 산출물 요약 형식

`_workspace/03_viewmodel_summary.md`:
```markdown
## 구현 완료 ViewModel
### ChatViewModel
- **프로퍼티**: InputText (string), IsBusy (bool), ErrorMessage (string)
- **컬렉션**: Messages (ObservableCollection<AgentMessage>)
- **커맨드**: SendCommand (AsyncRelayCommand), ClearCommand (RelayCommand)
- **생성자 주입**: IChatService
```
