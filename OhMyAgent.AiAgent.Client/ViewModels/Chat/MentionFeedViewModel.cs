using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Chat;

namespace OhMyAgent.AiAgent.Client.ViewModels.Chat;

/// <summary>
/// 멘션 알림 피드(§4.5). `/chat/mentions` 응답(삭제 제외)을 메시지 카드 리스트로 노출하고,
/// 항목 클릭 시 해당 방을 연다(셸에 RoomId 전달). 단일 의존 = <see cref="IChatRealtimeService"/>.
/// </summary>
public sealed partial class MentionFeedViewModel : ObservableObject
{
    private const int FeedLimit = 50;

    private readonly IChatRealtimeService _realtime;
    private readonly ChatIdentity _identity;

    /// <summary>멘션된 메시지 카드(최신순 — 서버 순서 유지).</summary>
    public ObservableCollection<ChatMessageViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public MentionFeedViewModel(IChatRealtimeService realtime, ChatIdentity identity)
    {
        _realtime = realtime;
        _identity = identity;
    }

    /// <summary>멘션 피드 로드.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        await UiInvokeAsync(() => { IsLoading = true; StatusMessage = string.Empty; }).ConfigureAwait(false);

        IReadOnlyList<ChatMessage> mentions;
        try
        {
            mentions = await _realtime.GetMentionsAsync(FeedLimit).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() =>
            {
                IsLoading = false;
                StatusMessage = string.IsNullOrWhiteSpace(ex.Message) ? "멘션을 불러오지 못했습니다." : ex.Message;
            }).ConfigureAwait(false);
            return;
        }

        await UiInvokeAsync(() =>
        {
            Items.Clear();
            foreach (var dto in mentions)
                Items.Add(new ChatMessageViewModel(dto, _identity)
                {
                    SenderName = _realtime.DisplayName(dto.SenderId),   // UUID → 표시이름
                });
            IsLoading = false;
        }).ConfigureAwait(false);
    }

    /// <summary>피드 항목 클릭 → 해당 방 열기(셸이 RoomId로 방을 연다).</summary>
    [RelayCommand]
    private void OpenMention(ChatMessageViewModel? item)
    {
        if (item is null || string.IsNullOrEmpty(item.RoomId)) return;
        // 메시지의 RoomId로 셸이 방을 연다.
        MentionOpenRequested?.Invoke(this, item.RoomId);
    }

    /// <summary>멘션 카드 클릭 시 셸이 해당 방(roomId)을 열도록 알림.</summary>
    public event EventHandler<string>? MentionOpenRequested;

    private static Task UiInvokeAsync(Action action) => UiDispatch.InvokeAsync(action);
}
