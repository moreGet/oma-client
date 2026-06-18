using CommunityToolkit.Mvvm.ComponentModel;

namespace OhMyAgent.AiAgent.Client.ViewModels.Transcript;

/// <summary>
/// A streaming assistant prose turn. <see cref="Text"/> is appended to as
/// <c>AgentTextDelta</c> events arrive; <see cref="IsStreaming"/> flips to
/// false once the assistant turn closes.
/// </summary>
public sealed partial class AssistantTurnViewModel : ObservableObject, ITranscriptItem
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming = true;
}
