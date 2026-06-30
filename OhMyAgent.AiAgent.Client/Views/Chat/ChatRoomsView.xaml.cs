using System;
using System.Linq;
using System.Windows;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using UserControl = System.Windows.Controls.UserControl;

namespace OhMyAgent.AiAgent.Client.Views.Chat;

/// <summary>
/// 좌측 방 목록(아바타/이름/미리보기/시각/안읽음 배지 + 새 대화). DataContext = ChatRoomsViewModel.
/// 새 대화(1:1/group) 입력 흐름은 code-behind 가 간단 입력 모달을 띄운 뒤 VM 커맨드를 인자와 함께 실행한다
/// (CreateDirectCommand=userId / CreateGroupCommand=(name, members)). userId/멤버는 모두 member UUID.
/// </summary>
public partial class ChatRoomsView : UserControl
{
    public ChatRoomsView() => InitializeComponent();

    private ChatRoomsViewModel? Vm => DataContext as ChatRoomsViewModel;

    /// <summary>"새 대화" 좌클릭 → 1:1/그룹 선택 메뉴를 연다(ContextMenu 는 우클릭 전용이므로 수동 오픈).</summary>
    private void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } btn)
        {
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>1:1 대화 — 상대 member UUID 입력 → CreateDirectCommand(userId).</summary>
    private void NewDirect_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var userId = ChatInputDialog.Prompt(
            Window.GetWindow(this),
            "1:1 대화",
            "상대방의 member ID(UUID)를 입력하세요.");
        if (string.IsNullOrWhiteSpace(userId)) return;

        if (vm.CreateDirectCommand.CanExecute(userId.Trim()))
            vm.CreateDirectCommand.Execute(userId.Trim());
    }

    /// <summary>그룹 대화 — 방 이름 + 콤마구분 member UUID 입력 → CreateGroupCommand((name, members)).</summary>
    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var owner = Window.GetWindow(this);

        var name = ChatInputDialog.Prompt(owner, "그룹 대화", "그룹 이름을 입력하세요.");
        if (string.IsNullOrWhiteSpace(name)) return;

        var membersRaw = ChatInputDialog.Prompt(
            owner,
            "그룹 멤버",
            "초대할 멤버의 member ID(UUID)를 콤마로 구분해 입력하세요.\n(본인은 자동 포함됩니다)");
        if (membersRaw is null) return;   // 취소(빈 입력은 멤버 없이 생성 시도 허용)

        var members = membersRaw
            .Split(new[] { ',', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var arg = (name.Trim(), (System.Collections.Generic.IReadOnlyList<string>)members);
        if (vm.CreateGroupCommand.CanExecute(arg))
            vm.CreateGroupCommand.Execute(arg);
    }
}
