using System;
using System.IO;
using System.Windows;
using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using OhMyAgent.AiAgent.Client.Views.Chat;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace OhMyAgent.AiAgent.Client.Views.Chat.Controls;

/// <summary>
/// 한 메시지 버블(좌/우, 멘션 하이라이트, 첨부 칩, 전송상태). DataContext = ChatMessageViewModel.
/// 룸 VM(EditCommand/DownloadAttachmentAsync 등)은 Border.Tag(=부모 ItemsControl.DataContext=ChatRoomViewModel)로 도달.
/// 인라인 편집(입력 모달) / 첨부 다운로드(SaveFileDialog) 는 code-behind 가 처리한다.
/// </summary>
public partial class ChatMessageBubble : UserControl
{
    public ChatMessageBubble() => InitializeComponent();

    private ChatMessageViewModel? Message => DataContext as ChatMessageViewModel;

    /// <summary>버블의 Tag(=부모 ItemsControl.DataContext)에서 룸 VM 을 얻는다.</summary>
    private ChatRoomViewModel? FindRoom(DependencyObject? from)
    {
        // 1) 컨텍스트 메뉴 경로: PlacementTarget(Border).Tag.
        var current = from;
        while (current is not null)
        {
            if (current is FrameworkElement fe && fe.Tag is ChatRoomViewModel viaTag)
                return viaTag;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                      ?? (current as FrameworkElement)?.Parent;
        }

        // 2) 폴백: 이 컨트롤 자신의 Tag(설정돼 있으면).
        return Tag as ChatRoomViewModel;
    }

    /// <summary>
    /// 남의 메시지/삭제된 메시지에서는 컨텍스트 메뉴 자체를 열지 않는다.
    /// (ContextMenu 에 Visibility=Collapsed 를 걸어도 Popup 은 열려 빈 상자가 깜빡인다.)
    /// </summary>
    private void Bubble_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (Message is { IsMine: true, IsDeleted: false }) return;
        e.Handled = true;
    }

    /// <summary>본인 메시지 수정 — 현재 본문을 채운 입력 모달 → EditCommand((id, content)).</summary>
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Message is not { } msg) return;
        if (msg.IsDeleted) return;

        // ContextMenu MenuItem 에서 PlacementTarget(Border) → Tag(룸 VM) 도달.
        ChatRoomViewModel? room = null;
        if (sender is MenuItem { Parent: ContextMenu ctx } && ctx.PlacementTarget is FrameworkElement target)
            room = target.Tag as ChatRoomViewModel ?? FindRoom(target);
        room ??= FindRoom(this);
        if (room is null) return;

        var edited = ChatInputDialog.Prompt(
            Window.GetWindow(this),
            "메시지 수정",
            "수정할 내용을 입력하세요.",
            msg.Content,
            multiline: true);
        if (string.IsNullOrWhiteSpace(edited)) return;
        if (string.Equals(edited.Trim(), msg.Content, StringComparison.Ordinal)) return;

        var arg = (msg.Id, edited.Trim());
        if (room.EditCommand.CanExecute(arg))
            room.EditCommand.Execute(arg);
    }

    /// <summary>첨부 칩 클릭 — SaveFileDialog → DownloadAttachmentAsync 로 저장.</summary>
    private async void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatAttachment att) return;
        if (string.IsNullOrEmpty(att.Id))
        {
            ((App)Application.Current).Dialogs.ShowMessage("첨부", "이 첨부는 다운로드할 수 없습니다(식별자 없음).");
            return;
        }

        var room = FindRoom(fe);
        if (room is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "첨부 저장",
            FileName = string.IsNullOrWhiteSpace(att.FileName) ? "download" : att.FileName,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await using var stream = await room.DownloadAttachmentAsync(att.Id);
            await using var file = File.Create(dialog.FileName);
            await stream.CopyToAsync(file);
        }
        catch (Exception ex)
        {
            ((App)Application.Current).Dialogs.ShowMessage("첨부", $"다운로드에 실패했습니다.\n{ex.Message}", DialogSeverity.Warning);
        }
    }
}
