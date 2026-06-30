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
/// 멤버 관리 패널(§4.6). 멤버 목록 + presence + 추가/강퇴/나가기(group 한정). 1:1방은 group 전용
/// 커맨드가 모두 비활성(CanExecute = room.Type==Group). 강퇴는 생성자 정보 부재로 항상 시도 가능하되
/// 서버 403을 graceful 메시지로 처리한다(§4.6 주석).
/// </summary>
public sealed partial class RoomMembersViewModel : ObservableObject, IDisposable
{
    private readonly IChatRealtimeService _realtime;
    private readonly ChatRoom _room;
    private readonly string _currentUserId;
    private readonly Action _unsubscribe;

    /// <summary>멤버 행(presence 포함). 멤버 추가/강퇴/나가기·presence 이벤트로 갱신.</summary>
    public ObservableCollection<RoomMemberViewModel> Members { get; } = [];

    /// <summary>이 방이 group인지(1:1이면 group 전용 커맨드 비활성/버튼 숨김).</summary>
    public bool IsGroup => _room.Type == ChatRoomType.Group;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>선택된 멤버(강퇴 대상). 없으면 KickCommand 비활성.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KickCommand))]
    private RoomMemberViewModel? _selectedMember;

    public RoomMembersViewModel(IChatRealtimeService realtime, ChatRoom room, string currentUserId)
    {
        _realtime = realtime;
        _room = room;
        _currentUserId = currentUserId;

        _realtime.MemberJoined += OnMemberJoined;
        _realtime.MemberLeft += OnMemberLeft;
        _realtime.PresenceChanged += OnPresenceChanged;
        _realtime.DirectoryUpdated += OnDirectoryUpdated;
        _unsubscribe = () =>
        {
            _realtime.MemberJoined -= OnMemberJoined;
            _realtime.MemberLeft -= OnMemberLeft;
            _realtime.PresenceChanged -= OnPresenceChanged;
            _realtime.DirectoryUpdated -= OnDirectoryUpdated;
        };
    }

    /// <summary>디렉터리 갱신 → 멤버 표시이름 재해석(UUID→이름).</summary>
    private void OnDirectoryUpdated(object? sender, EventArgs e)
        => _ = UiInvokeAsync(() =>
        {
            foreach (var m in Members)
                m.DisplayName = _realtime.DisplayName(m.MemberId);
        });

    /// <summary>멤버 목록 + presence 로드. 패널 열람 시 호출.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        await UiInvokeAsync(() => { IsLoading = true; StatusMessage = string.Empty; }).ConfigureAwait(false);

        IReadOnlyList<string> members;
        IReadOnlyList<string> online;
        try
        {
            members = await _realtime.GetMembersAsync(_room.Id).ConfigureAwait(false);
            online = await _realtime.GetPresenceAsync(_room.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() =>
            {
                IsLoading = false;
                StatusMessage = DescribeFailure(ex);
            }).ConfigureAwait(false);
            return;
        }

        var onlineSet = new HashSet<string>(online, StringComparer.Ordinal);
        await UiInvokeAsync(() =>
        {
            Members.Clear();
            foreach (var id in members)
                Members.Add(new RoomMemberViewModel(
                    id,
                    _realtime.DisplayName(id),
                    isMe: string.Equals(id, _currentUserId, StringComparison.Ordinal),
                    presence: onlineSet.Contains(id) ? RoomMemberPresence.Online : RoomMemberPresence.Offline));
            IsLoading = false;
        }).ConfigureAwait(false);
    }

    private bool CanManageGroup() => IsGroup;

    /// <summary>멤버 추가(group 한정). memberIds는 View가 수집(다이얼로그/입력).</summary>
    [RelayCommand(CanExecute = nameof(CanManageGroup))]
    private async Task AddMembersAsync(IReadOnlyList<string>? memberIds)
    {
        if (memberIds is not { Count: > 0 }) return;

        try
        {
            await _realtime.AddMembersAsync(_room.Id, memberIds).ConfigureAwait(false);
            await UiInvokeAsync(() => StatusMessage = string.Empty).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = DescribeFailure(ex)).ConfigureAwait(false);
        }

        await LoadAsync().ConfigureAwait(false);
    }

    private bool CanKick() => IsGroup
        && SelectedMember is { IsMe: false } m
        && !string.IsNullOrEmpty(m.MemberId);

    /// <summary>선택 멤버 강퇴(group, 생성자만 — 서버가 비생성자면 403 → graceful 메시지).</summary>
    [RelayCommand(CanExecute = nameof(CanKick))]
    private async Task KickAsync()
    {
        if (SelectedMember is not { IsMe: false } target) return;

        try
        {
            await _realtime.KickMemberAsync(_room.Id, target.MemberId).ConfigureAwait(false);
            await UiInvokeAsync(() => StatusMessage = string.Empty).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 비생성자(403) 등 — 로그아웃하지 않고 안내만.
            await UiInvokeAsync(() => StatusMessage = DescribeFailure(ex)).ConfigureAwait(false);
            return;
        }

        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>이 방을 나간다(group 한정). 성공 시 상위가 방을 닫도록 이벤트 발화.</summary>
    [RelayCommand(CanExecute = nameof(CanManageGroup))]
    private async Task LeaveAsync()
    {
        try
        {
            await _realtime.LeaveRoomAsync(_room.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = DescribeFailure(ex)).ConfigureAwait(false);
            return;
        }

        await UiInvokeAsync(() => Left?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
    }

    /// <summary>방 나가기 성공 → 상위(ChatRoomViewModel/셸)가 방을 닫는다.</summary>
    public event EventHandler? Left;

    // ── Realtime 이벤트 구독(UI 마샬) ──────────────────────────────────

    private void OnMemberJoined(object? sender, WsMemberPayload p)
    {
        if (!string.Equals(p.RoomId, _room.Id, StringComparison.Ordinal)) return;
        _ = UiInvokeAsync(() =>
        {
            if (Members.Any(m => string.Equals(m.MemberId, p.MemberId, StringComparison.Ordinal))) return;
            Members.Add(new RoomMemberViewModel(
                p.MemberId,
                _realtime.DisplayName(p.MemberId),
                isMe: string.Equals(p.MemberId, _currentUserId, StringComparison.Ordinal),
                presence: RoomMemberPresence.Offline));
        });
    }

    private void OnMemberLeft(object? sender, WsMemberPayload p)
    {
        if (!string.Equals(p.RoomId, _room.Id, StringComparison.Ordinal)) return;
        _ = UiInvokeAsync(() =>
        {
            var existing = Members.FirstOrDefault(m => string.Equals(m.MemberId, p.MemberId, StringComparison.Ordinal));
            if (existing is not null) Members.Remove(existing);
        });
    }

    private void OnPresenceChanged(object? sender, WsPresencePayload p)
    {
        _ = UiInvokeAsync(() =>
        {
            var member = Members.FirstOrDefault(m => string.Equals(m.MemberId, p.MemberId, StringComparison.Ordinal));
            if (member is not null)
                member.Presence = p.Online ? RoomMemberPresence.Online : RoomMemberPresence.Offline;
        });
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        _ => string.IsNullOrWhiteSpace(ex.Message) ? "요청을 처리하지 못했습니다." : ex.Message,
    };

    private static Task UiInvokeAsync(Action action) => UiDispatch.InvokeAsync(action);

    public void Dispose() => _unsubscribe();
}

/// <summary>멤버 행(§4.6). presence 점·내 자신 여부.</summary>
public sealed partial class RoomMemberViewModel : ObservableObject
{
    public string MemberId { get; }

    /// <summary>표시이름(해석 캐시). 미해석 시 UUID 앞자리.</summary>
    [ObservableProperty] private string _displayName;

    /// <summary>나 자신 여부(강퇴 불가).</summary>
    public bool IsMe { get; }

    [ObservableProperty] private RoomMemberPresence _presence;

    public RoomMemberViewModel(string memberId, string displayName, bool isMe, RoomMemberPresence presence)
    {
        MemberId = memberId;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? memberId : displayName;
        IsMe = isMe;
        _presence = presence;
    }
}

/// <summary>멤버 온라인 상태(§4.6).</summary>
public enum RoomMemberPresence { Offline, Online }
