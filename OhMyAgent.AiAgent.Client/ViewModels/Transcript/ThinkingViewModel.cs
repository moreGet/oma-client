using CommunityToolkit.Mvvm.ComponentModel;

namespace OhMyAgent.AiAgent.Client.ViewModels.Transcript;

/// <summary>
/// 에이전트의 확장 사고 블록. <c>AgentThinkingDelta</c> 가 도착하는 동안 <see cref="Text"/> 에 누적되고,
/// 사고가 닫히면(<c>AgentThinkingComplete</c>) <see cref="IsStreaming"/> 가 false 가 된다.
///
/// 최종 답변과 시각적으로 구분한다 — 흐릿하게, 접을 수 있게. 사고는 "과정"이지 "결론"이 아니므로
/// 사용자가 흐름은 볼 수 있되 최종 답을 압도하지 않아야 한다. 스트리밍이 끝나면 기본 접힘.
/// </summary>
public sealed partial class ThinkingViewModel : ObservableObject, ITranscriptItem
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming = true;

    /// <summary>펼침 여부. 스트리밍 중에는 펼쳐 흐름을 보여주고, 끝나면 접어 결론을 가리지 않는다.</summary>
    [ObservableProperty] private bool _isExpanded = true;
}
