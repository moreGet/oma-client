using CommunityToolkit.Mvvm.ComponentModel;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.ViewModels.Transcript;

/// <summary>Lifecycle status of a tool call surfaced in the transcript.</summary>
public enum ToolCallStatus
{
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Denied,
}

/// <summary>
/// A collapsible tool-call card. Shows the tool name, its risk, a JSON args
/// preview, the running/done/error status and the tool result.
/// </summary>
public sealed partial class ToolCallViewModel : ObservableObject, ITranscriptItem
{
    public string CallId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public ToolRisk Risk { get; init; }

    /// <summary>Pretty-printed JSON arguments.</summary>
    public string ArgsPreview { get; init; } = "";

    [ObservableProperty] private ToolCallStatus _status = ToolCallStatus.Running;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isExpanded;
}
