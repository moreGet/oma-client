using System.Windows;
using System.Windows.Controls;
using OhMyAgent.AiAgent.Client.ViewModels;

namespace OhMyAgent.AiAgent.Client.Views;

public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate  { get; set; }
    public DataTemplate? AgentTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is ChatMessageViewModel { IsUser: true } ? UserTemplate : AgentTemplate;
}
