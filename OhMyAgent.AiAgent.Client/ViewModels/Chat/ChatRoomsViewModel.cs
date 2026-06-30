using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Chat;

namespace OhMyAgent.AiAgent.Client.ViewModels.Chat;

/// <summary>
/// 좌측 방 목록(§4.2). 안읽음 배지 + 방 생성(1:1/group) + 선택. 최근활동순(lastActivityAt desc) 정렬.
/// realtime.RoomUpserted/UnreadChanged 구독 → 해당 item 갱신 + 재정렬. 단일 의존 = <see cref="IChatRealtimeService"/>.
/// </summary>
public sealed partial class ChatRoomsViewModel : ObservableObject, IDisposable
{
    private readonly IChatRealtimeService _realtime;
    private readonly Action _unsubscribe;

    /// <summary>방 목록 행(최근활동순).</summary>
    public ObservableCollection<ChatRoomListItemViewModel> Rooms { get; } = [];

    /// <summary>선택된 방. 셸 VM이 구독해 CurrentRoom을 교체.</summary>
    [ObservableProperty] private ChatRoomListItemViewModel? _selectedRoom;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ChatRoomsViewModel(IChatRealtimeService realtime)
    {
        _realtime = realtime;

        _realtime.RoomUpserted += OnRoomUpserted;
        _realtime.UnreadChanged += OnUnreadChanged;
        _realtime.DirectoryUpdated += OnDirectoryUpdated;
        _unsubscribe = () =>
        {
            _realtime.RoomUpserted -= OnRoomUpserted;
            _realtime.UnreadChanged -= OnUnreadChanged;
            _realtime.DirectoryUpdated -= OnDirectoryUpdated;
        };
    }

    /// <summary>1:1 방의 표시이름을 상대 memberId 의 해석 이름으로 채운다(없으면 GetDirectCounterpart 로 상대 id 조회).</summary>
    private async Task ResolveDirectNameAsync(ChatRoomListItemViewModel item)
    {
        if (item.Type != ChatRoomType.Direct) return;
        try
        {
            var counterpart = item.CounterpartId ?? await _realtime.GetDirectCounterpartAsync(item.Id).ConfigureAwait(false);
            if (string.IsNullOrEmpty(counterpart)) return;
            await UiInvokeAsync(() =>
            {
                item.CounterpartId = counterpart;
                item.DisplayName = _realtime.DisplayName(counterpart);
            }).ConfigureAwait(false);
        }
        catch { /* 상대 해석 실패 시 "1:1 대화" 유지 */ }
    }

    /// <summary>디렉터리 캐시 갱신 → 1:1 방 표시이름 재해석(UUID→이름).</summary>
    private void OnDirectoryUpdated(object? sender, EventArgs e)
        => _ = UiInvokeAsync(() =>
        {
            foreach (var item in Rooms)
                if (item.Type == ChatRoomType.Direct && !string.IsNullOrEmpty(item.CounterpartId))
                    item.DisplayName = _realtime.DisplayName(item.CounterpartId!);
        });

    /// <summary>방 목록 로드(안읽음 포함). 셸 StartAsync 후 호출.</summary>
    [RelayCommand]
    private async Task LoadRoomsAsync()
    {
        await UiInvokeAsync(() => { IsLoading = true; StatusMessage = string.Empty; }).ConfigureAwait(false);

        IReadOnlyList<ChatRoom> rooms;
        try
        {
            rooms = await _realtime.LoadRoomsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => { IsLoading = false; StatusMessage = Describe(ex); }).ConfigureAwait(false);
            return;
        }

        var directItems = new List<ChatRoomListItemViewModel>();
        await UiInvokeAsync(() =>
        {
            Rooms.Clear();
            foreach (var r in rooms)
            {
                var item = new ChatRoomListItemViewModel(r);
                Rooms.Add(item);
                if (item.Type == ChatRoomType.Direct) directItems.Add(item);
            }
            Resort();
            IsLoading = false;
        }).ConfigureAwait(false);

        // 1:1 방 표시이름 해석(상대 memberId → 이름). 방 수만큼 GetMembers — 보통 소수.
        foreach (var item in directItems)
            await ResolveDirectNameAsync(item).ConfigureAwait(false);
    }

    /// <summary>1:1 방 가져오기/생성(중복방지). 성공 시 RoomOpenRequested로 셸에 알림.</summary>
    [RelayCommand]
    private async Task CreateDirectAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        try
        {
            var room = await _realtime.OpenDirectRoomAsync(userId).ConfigureAwait(false);
            await UiInvokeAsync(() =>
            {
                var item = Upsert(room);
                item.CounterpartId = userId;                 // 생성 시 상대 id 를 알고 있음
                item.DisplayName = _realtime.DisplayName(userId);
                RoomOpenRequested?.Invoke(this, item);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = Describe(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>단체방 생성(생성자 자동포함). arg=(name, memberIds).</summary>
    [RelayCommand]
    private async Task CreateGroupAsync((string name, IReadOnlyList<string> members) arg)
    {
        if (string.IsNullOrWhiteSpace(arg.name) || arg.members is not { Count: > 0 }) return;
        try
        {
            var room = await _realtime.CreateGroupRoomAsync(arg.name.Trim(), arg.members).ConfigureAwait(false);
            await UiInvokeAsync(() =>
            {
                var item = Upsert(room);
                RoomOpenRequested?.Invoke(this, item);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = Describe(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>방 생성/선택 시 셸이 해당 방을 열도록 알림.</summary>
    public event EventHandler<ChatRoomListItemViewModel>? RoomOpenRequested;

    /// <summary>목록에서 방을 선택하면 셸이 해당 방을 연다(ListBox SelectedItem TwoWay 바인딩 → 자동 오픈).</summary>
    partial void OnSelectedRoomChanged(ChatRoomListItemViewModel? value)
    {
        if (value is not null)
            RoomOpenRequested?.Invoke(this, value);
    }

    // ── realtime 이벤트(UI 마샬) ───────────────────────────────────────

    private void OnRoomUpserted(object? sender, ChatRoom room)
        => _ = UiInvokeAsync(() => { Upsert(room); Resort(); });

    private void OnUnreadChanged(object? sender, ChatUnreadResponse unread)
    {
        // 서버가 안읽음 0 상태에서 rooms 키를 생략하면 unread.Rooms 가 null 로 역직렬화될 수 있으므로 방어.
        var perRoom = unread.Rooms;
        _ = UiInvokeAsync(() =>
        {
            foreach (var item in Rooms)
                item.UnreadCount = perRoom is not null && perRoom.TryGetValue(item.Id, out var c) ? c : 0;
            Resort();
        });
    }

    /// <summary>방 행 upsert(있으면 갱신, 없으면 추가). UI 스레드에서 호출.</summary>
    private ChatRoomListItemViewModel Upsert(ChatRoom room)
    {
        var existing = Rooms.FirstOrDefault(r => string.Equals(r.Id, room.Id, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.ApplyRoom(room);
            return existing;
        }

        var item = new ChatRoomListItemViewModel(room);
        Rooms.Add(item);
        return item;
    }

    /// <summary>최근활동순(lastActivityAt desc) 재정렬. UI 스레드에서 호출(컬렉션 재배치).</summary>
    private void Resort()
    {
        var ordered = Rooms.OrderByDescending(r => r.LastActivityAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Rooms.IndexOf(ordered[i]);
            if (current != i) Rooms.Move(current, i);
        }
    }

    private static string Describe(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? "요청을 처리하지 못했습니다." : ex.Message;

    private static Task UiInvokeAsync(Action action) => UiDispatch.InvokeAsync(action);

    public void Dispose() => _unsubscribe();
}

/// <summary>방 목록 한 행(§4.2). 안읽음 배지 = UnreadCount &gt; 0.</summary>
public sealed partial class ChatRoomListItemViewModel : ObservableObject
{
    public string Id { get; }
    public ChatRoomType Type { get; }

    /// <summary>1:1 방의 상대 memberId(이름 해석용). group/미해석 시 null.</summary>
    public string? CounterpartId { get; set; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private string _lastPreview = string.Empty;
    [ObservableProperty] private long _lastActivityAt;

    /// <summary>안읽음 배지 표시(UnreadCount &gt; 0). XAML은 CountToVisibility로 직접 바인딩 가능.</summary>
    public bool HasUnread => UnreadCount > 0;

    public ChatRoomListItemViewModel(ChatRoom room)
    {
        Id = room.Id;
        Type = room.Type;
        _displayName = string.IsNullOrWhiteSpace(room.Name) ? "1:1 대화" : room.Name!;
        _unreadCount = room.UnreadCount;
        _lastActivityAt = room.CreatedAt;
    }

    /// <summary>서버 DTO로 갱신(unread/이름/활동시각). UI 스레드에서 호출.</summary>
    public void ApplyRoom(ChatRoom room)
    {
        DisplayName = string.IsNullOrWhiteSpace(room.Name) ? "1:1 대화" : room.Name!;
        UnreadCount = room.UnreadCount;
        if (room.CreatedAt > LastActivityAt) LastActivityAt = room.CreatedAt;
    }

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));
}
