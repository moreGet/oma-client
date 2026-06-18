using CommunityToolkit.Mvvm.ComponentModel;

namespace OhMyAgent.AiAgent.Client.ViewModels.Transcript;

/// <summary>A single user message bubble in the transcript.</summary>
public sealed partial class UserTurnViewModel : ObservableObject, ITranscriptItem
{
    public string Text { get; init; } = "";
}
