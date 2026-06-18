using CommunityToolkit.Mvvm.ComponentModel;

namespace OhMyAgent.AiAgent.Client.ViewModels.Transcript;

/// <summary>A system / informational notice in the transcript (greetings, errors, status).</summary>
public sealed partial class SystemNoticeViewModel : ObservableObject, ITranscriptItem
{
    public string Text { get; init; } = "";
}
