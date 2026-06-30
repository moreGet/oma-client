using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using UserControl = System.Windows.Controls.UserControl;

namespace OhMyAgent.AiAgent.Client.Views.Chat;

/// <summary>멤버 리스트 + presence dot, group이면 추가/강퇴/나가기. DataContext = RoomMembersViewModel.</summary>
public partial class RoomMembersView : UserControl
{
    public RoomMembersView() => InitializeComponent();

    /// <summary>멤버 추가 — 콤마 구분 member UUID 입력 모달 → AddMembersCommand(list).</summary>
    private void AddMembers_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RoomMembersViewModel vm) return;

        var raw = ChatInputDialog.Prompt(
            Window.GetWindow(this),
            "멤버 추가",
            "추가할 멤버의 member ID(UUID)를 콤마로 구분해 입력하세요.");
        if (string.IsNullOrWhiteSpace(raw)) return;

        var ids = raw
            .Split(new[] { ',', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return;

        var arg = (IReadOnlyList<string>)ids;
        if (vm.AddMembersCommand.CanExecute(arg))
            vm.AddMembersCommand.Execute(arg);
    }
}
