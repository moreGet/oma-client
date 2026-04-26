using CommunityToolkit.Mvvm.ComponentModel;

namespace OhMyAgent.AiAgent.Client.ViewModels;

public partial class ChatMessageViewModel : ObservableObject
{
    [ObservableProperty] private string  _content     = string.Empty;
    [ObservableProperty] private bool    _isUser;
    [ObservableProperty] private bool    _isStreaming;

    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}
