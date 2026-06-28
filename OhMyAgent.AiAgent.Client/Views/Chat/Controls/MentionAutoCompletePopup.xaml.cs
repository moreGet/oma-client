using UserControl = System.Windows.Controls.UserControl;

namespace OhMyAgent.AiAgent.Client.Views.Chat.Controls;

/// <summary>@멘션 자동완성 Popup. DataContext = MentionAutoCompleteViewModel.</summary>
public partial class MentionAutoCompletePopup : UserControl
{
    public MentionAutoCompletePopup() => InitializeComponent();
}
