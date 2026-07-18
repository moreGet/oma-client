using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Services;

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

    /// <summary>이 응답의 원문(마크다운 그대로)을 클립보드에 복사. 렌더된 응답은 통째로 선택이 안 되므로 버튼으로 제공.</summary>
    [RelayCommand]
    private void Copy() => ClipboardSafe.TrySetText(Text);
}
