using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Chat;

namespace OhMyAgent.AiAgent.Client.ViewModels.Chat;

/// <summary>
/// 메신저 셸 VM(§4.1). 좌(방목록)+우(현재방) 보유, 연결상태, 전역 안읽음, 멘션피드/멤버관리 토글.
/// realtime의 집계 이벤트(ConnectionStateChanged/UnreadChanged/OperationFailed/AuthExpired)를 구독해
/// 상위로 재노출한다. 401(AuthExpired)은 표시하지 않고 <see cref="LoginRequested"/>로 App에 위임,
/// 403/404/429/5xx(OperationFailed)는 <see cref="StatusMessage"/>로만 표시(§7). 단일 의존 = <see cref="IChatRealtimeService"/>.
/// </summary>
public sealed partial class ChatMessengerViewModel : ObservableObject, IDisposable
{
    private readonly IChatRealtimeService _realtime;

    /// <summary>현재 사용자 id. 설계서에 공급원 미명시 → ctor 주입(상위 App이 프로필 /users/me에서 공급).</summary>
    private readonly string _currentUserId;

    private readonly Action _unsubscribe;

    /// <summary>좌측 방 목록 VM.</summary>
    public ChatRoomsViewModel Rooms { get; }

    /// <summary>멘션 피드 VM.</summary>
    public MentionFeedViewModel MentionFeed { get; }

    /// <summary>현재 열린 대화방(없으면 null). 방 선택/생성 시 교체.</summary>
    [ObservableProperty] private ChatRoomViewModel? _currentRoom;

    /// <summary>WS 연결 상태(타이틀바 상태점).</summary>
    [ObservableProperty] private ChatConnectionState _connectionState = ChatConnectionState.Disconnected;

    /// <summary>전역 안읽음 합계(타이틀바 Pill 배지).</summary>
    [ObservableProperty] private int _totalUnread;

    /// <summary>멘션 피드 패널 열림 여부(우측 영역을 피드로 토글).</summary>
    [ObservableProperty] private bool _isMentionFeedOpen;

    /// <summary>403/404/429/5xx 등 상태 메시지(표시만, 로그아웃 금지).</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ChatMessengerViewModel(IChatRealtimeService realtime, string currentUserId)
    {
        _realtime = realtime;
        _currentUserId = currentUserId;

        Rooms = new ChatRoomsViewModel(realtime);
        MentionFeed = new MentionFeedViewModel(realtime, currentUserId);

        Rooms.RoomOpenRequested += OnRoomOpenRequested;
        MentionFeed.MentionOpenRequested += OnMentionOpenRequested;

        _realtime.ConnectionStateChanged += OnConnectionStateChanged;
        _realtime.UnreadChanged += OnUnreadChanged;
        _realtime.OperationFailed += OnOperationFailed;
        _realtime.AuthExpired += OnAuthExpired;
        _unsubscribe = () =>
        {
            Rooms.RoomOpenRequested -= OnRoomOpenRequested;
            MentionFeed.MentionOpenRequested -= OnMentionOpenRequested;
            _realtime.ConnectionStateChanged -= OnConnectionStateChanged;
            _realtime.UnreadChanged -= OnUnreadChanged;
            _realtime.OperationFailed -= OnOperationFailed;
            _realtime.AuthExpired -= OnAuthExpired;
        };
    }

    // ── 수명주기 ──────────────────────────────────────────────────────

    /// <summary>창 최초 표시 시 1회 — WS connect + unread 초기로드 + 방목록 로드(§6 수명주기).</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        try
        {
            await _realtime.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = Describe(ex)).ConfigureAwait(false);
        }

        await Rooms.LoadRoomsCommand.ExecuteAsync(null).ConfigureAwait(false);
    }

    // ── 방 열기 ────────────────────────────────────────────────────────

    /// <summary>방 목록 더블클릭/선택 → 해당 방 열기 + 읽음 처리.</summary>
    [RelayCommand]
    private async Task OpenRoomAsync(ChatRoomListItemViewModel? item)
    {
        if (item is null) return;
        await OpenRoomByIdAsync(item.Id, item.DisplayName, item.Type).ConfigureAwait(false);
    }

    /// <summary>1:1 방 열기(없으면 생성). userId로 진입(외부 진입점).</summary>
    [RelayCommand]
    private async Task OpenDirectAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        try
        {
            var room = await _realtime.OpenDirectRoomAsync(userId).ConfigureAwait(false);
            await SwitchRoomAsync(room).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = Describe(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>멘션 피드 패널 토글(열 때 피드 로드).</summary>
    [RelayCommand]
    private async Task ToggleMentionFeedAsync()
    {
        await UiInvokeAsync(() => IsMentionFeedOpen = !IsMentionFeedOpen).ConfigureAwait(false);
        if (IsMentionFeedOpen)
            await MentionFeed.LoadCommand.ExecuteAsync(null).ConfigureAwait(false);
    }

    /// <summary>roomId로 방을 연다(목록에 있으면 메타 사용, 없으면 목록 로드 후 탐색).</summary>
    private async Task OpenRoomByIdAsync(string roomId, string? displayName, ChatRoomType? type)
    {
        // 목록에서 ChatRoom DTO를 직접 얻을 수 없으므로(아이템 VM만 보유) 합성 DTO로 방 VM을 구성한다.
        // ChatRoomViewModel은 InitializeAsync에서 실제 메시지/멤버를 서버에서 다시 로드한다.
        var found = Rooms.Rooms.FirstOrDefault(r => string.Equals(r.Id, roomId, StringComparison.Ordinal));
        var room = new ChatRoom(
            roomId,
            type ?? found?.Type ?? ChatRoomType.Group,
            displayName ?? found?.DisplayName,
            found?.LastActivityAt ?? 0,
            0);

        await SwitchRoomAsync(room).ConfigureAwait(false);
    }

    /// <summary>현재 방을 교체(이전 방 Dispose) + 새 방 초기화 + 멘션 피드 닫기.</summary>
    private async Task SwitchRoomAsync(ChatRoom room)
    {
        ChatRoomViewModel? previous = null;
        ChatRoomViewModel next = null!;
        await UiInvokeAsync(() =>
        {
            previous = CurrentRoom;
            next = new ChatRoomViewModel(_realtime, room, _currentUserId);
            next.Members.Left += OnRoomLeft;
            CurrentRoom = next;
            IsMentionFeedOpen = false;
        }).ConfigureAwait(false);

        _realtime.SetActiveRoom(room.Id);   // 이 방 수신분은 안읽음 증가 제외(보는 중)

        if (previous is not null)
        {
            previous.Members.Left -= OnRoomLeft;
            previous.Dispose();
        }

        await next.InitializeCommand.ExecuteAsync(null).ConfigureAwait(false);
    }

    private void OnRoomLeft(object? sender, EventArgs e)
    {
        _realtime.SetActiveRoom(null);
        _ = UiInvokeAsync(() =>
        {
            if (CurrentRoom is { } room)
            {
                room.Members.Left -= OnRoomLeft;
                room.Dispose();
                CurrentRoom = null;
            }
        });
    }

    // ── realtime/child 이벤트(UI 마샬) ─────────────────────────────────

    private void OnRoomOpenRequested(object? sender, ChatRoomListItemViewModel item)
        => _ = OpenRoomByIdAsync(item.Id, item.DisplayName, item.Type);

    private void OnMentionOpenRequested(object? sender, string roomId)
        => _ = OpenRoomByIdAsync(roomId, displayName: null, type: null);

    private void OnConnectionStateChanged(object? sender, ChatConnectionState state)
        => _ = UiInvokeAsync(() => ConnectionState = state);

    private void OnUnreadChanged(object? sender, ChatUnreadResponse unread)
        => _ = UiInvokeAsync(() => TotalUnread = unread.Total);

    private void OnOperationFailed(object? sender, ChatOperationError error)
        => _ = UiInvokeAsync(() => StatusMessage = FriendlyError(error));

    /// <summary>401 — 표시하지 않고 상위(App)로. ApplyReadiness/ReturnToLogin이 로그인 화면 회귀 처리.</summary>
    private void OnAuthExpired(object? sender, EventArgs e)
        => _ = UiInvokeAsync(() => LoginRequested?.Invoke(this, EventArgs.Empty));

    /// <summary>AuthExpired 재노출(§4.1) — App.xaml.cs가 구독해 ReturnToLogin()을 호출.</summary>
    public event EventHandler? LoginRequested;

    /// <summary>상태코드별 친화 문구(표시만, 로그아웃 금지).</summary>
    private static string FriendlyError(ChatOperationError error) => error.StatusCode switch
    {
        403 => "권한이 없습니다.",
        404 => "사용할 수 없는 기능입니다.",
        429 => "요청이 많습니다. 잠시 후 다시 시도하세요.",
        >= 500 => "서버 오류입니다. 잠시 후 다시 시도하세요.",
        _ => string.IsNullOrWhiteSpace(error.Message) ? "요청을 처리하지 못했습니다." : error.Message,
    };

    private static string Describe(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? "요청을 처리하지 못했습니다." : ex.Message;

    private static Task UiInvokeAsync(Action action) => UiDispatch.InvokeAsync(action);

    public void Dispose()
    {
        _unsubscribe();
        if (CurrentRoom is { } room)
        {
            room.Members.Left -= OnRoomLeft;
            room.Dispose();
        }
        Rooms.Dispose();
    }
}
