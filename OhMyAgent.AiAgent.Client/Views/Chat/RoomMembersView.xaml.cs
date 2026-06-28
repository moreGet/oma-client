using UserControl = System.Windows.Controls.UserControl;

namespace OhMyAgent.AiAgent.Client.Views.Chat;

/// <summary>멤버 리스트 + presence dot, group이면 추가/강퇴/나가기. DataContext = RoomMembersViewModel.</summary>
public partial class RoomMembersView : UserControl
{
    public RoomMembersView() => InitializeComponent();
}
