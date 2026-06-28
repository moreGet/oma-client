using UserControl = System.Windows.Controls.UserControl;

namespace OhMyAgent.AiAgent.Client.Views.Chat;

/// <summary>멘션 피드 카드 리스트(클릭 → 방 이동). DataContext = MentionFeedViewModel.</summary>
public partial class MentionFeedView : UserControl
{
    public MentionFeedView() => InitializeComponent();
}
