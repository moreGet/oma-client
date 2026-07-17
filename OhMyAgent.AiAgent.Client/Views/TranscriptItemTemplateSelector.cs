using System.Windows;
using System.Windows.Controls;
using OhMyAgent.AiAgent.Client.ViewModels.Transcript;

namespace OhMyAgent.AiAgent.Client.Views;

/// <summary>
/// Selects a <see cref="DataTemplate"/> for each <see cref="ITranscriptItem"/> in the
/// agent transcript, keyed off the concrete ViewModel type.
/// </summary>
public sealed class TranscriptItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? SystemTemplate { get; set; }
    public DataTemplate? ToolCallTemplate { get; set; }
    public DataTemplate? ThinkingTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
        => item switch
        {
            UserTurnViewModel      => UserTemplate,
            AssistantTurnViewModel => AssistantTemplate,
            SystemNoticeViewModel  => SystemTemplate,
            ToolCallViewModel      => ToolCallTemplate,
            ThinkingViewModel      => ThinkingTemplate,
            _                      => base.SelectTemplate(item, container),
        };
}
