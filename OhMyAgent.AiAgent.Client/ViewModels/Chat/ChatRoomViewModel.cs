using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Chat;
using Timer = System.Timers.Timer;

namespace OhMyAgent.AiAgent.Client.ViewModels.Chat;

/// <summary>
/// 한 대화방(§4.3). 메시지 컬렉션, 무한스크롤(before), 입력/전송(낙관 렌더), typing 디바운스,
/// 읽음 마킹/표시, 멘션 자동완성, 첨부를 담당한다. 단일 의존 = <see cref="IChatRealtimeService"/>.
/// realtime 집계 이벤트(MessageUpserted/ReadChanged/TypingChanged/PresenceChanged)를 구독하고
/// 모든 컬렉션/프로퍼티 변경을 <see cref="UiDispatch.InvokeAsync"/>로 마샬한다.
/// </summary>
public sealed partial class ChatRoomViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 50;
    private const double TypingDebounceMs = 1500;   // 무입력 N초 후 stop 전송

    private readonly IChatRealtimeService _realtime;
    private readonly ChatRoom _room;
    private readonly string _currentUserId;
    private readonly Action _unsubscribe;

    // typing 디바운스 — Draft 변경 시 start(쓰로틀) + 무입력 N초/전송 시 stop.
    private readonly Timer _typingTimer;
    private bool _typingActive;

    /// <summary>임시(낙관) 메시지를 서버 echo와 매칭하기 위한 clientLocalId → VM 매핑.</summary>
    private readonly Dictionary<string, ChatMessageViewModel> _pendingLocal = new(StringComparer.Ordinal);

    /// <summary>방 식별/표시(헤더 바인딩).</summary>
    public string RoomId => _room.Id;
    public ChatRoomType RoomType => _room.Type;
    public string DisplayName => string.IsNullOrWhiteSpace(_room.Name) ? "1:1 대화" : _room.Name!;

    /// <summary>메시지 버블(오래된→최신 순). 상단 prepend=이력, 하단 append=신규.</summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    /// <summary>입력 컴포저 본문. setter에서 typing 디바운스 + 멘션 자동완성 트리거.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    /// <summary>더 불러올 과거 이력이 있는지(응답&lt;limit이면 false).</summary>
    [ObservableProperty] private bool _hasMoreHistory = true;

    /// <summary>상단 이력 로딩 중(스피너).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _isLoadingMore;

    /// <summary>최초 메시지 로드 중.</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>현재 typing 중인 타 멤버 id 목록.</summary>
    public ObservableCollection<string> TypingMembers { get; } = [];

    /// <summary>typing 인디케이터 표시 문자열(계산 — TypingMembers 변동 시 갱신).</summary>
    [ObservableProperty] private string _typingIndicatorText = string.Empty;

    /// <summary>온라인 멤버 id(헤더 "N명 온라인").</summary>
    public ObservableCollection<string> OnlinePresence { get; } = [];

    /// <summary>멤버 관리 패널 VM(group 한정 커맨드).</summary>
    public RoomMembersViewModel Members { get; }

    /// <summary>전송 대기 첨부 칩(업로드 완료분). 전송 시 본문과 함께 실린다.</summary>
    public ObservableCollection<ChatAttachment> PendingAttachments { get; } = [];

    /// <summary>@멘션 자동완성 Popup VM.</summary>
    public MentionAutoCompleteViewModel Mentions { get; } = new();

    /// <summary>전송에 포함할 멘션 memberId 누적(자동완성 선택분).</summary>
    private readonly List<string> _draftMentions = [];

    [ObservableProperty] private string _statusMessage = string.Empty;

    public ChatRoomViewModel(IChatRealtimeService realtime, ChatRoom room, string currentUserId)
    {
        _realtime = realtime;
        _room = room;
        _currentUserId = currentUserId;
        Members = new RoomMembersViewModel(realtime, room, currentUserId);

        _typingTimer = new Timer(TypingDebounceMs) { AutoReset = false };
        _typingTimer.Elapsed += OnTypingTimerElapsed;

        Mentions.MemberSelected += OnMentionSelected;
        TypingMembers.CollectionChanged += (_, _) => UpdateTypingIndicator();

        _realtime.MessageUpserted += OnMessageUpserted;
        _realtime.ReadChanged += OnReadChanged;
        _realtime.TypingChanged += OnTypingChanged;
        _realtime.PresenceChanged += OnPresenceChanged;
        _unsubscribe = () =>
        {
            _realtime.MessageUpserted -= OnMessageUpserted;
            _realtime.ReadChanged -= OnReadChanged;
            _realtime.TypingChanged -= OnTypingChanged;
            _realtime.PresenceChanged -= OnPresenceChanged;
        };
    }

    // ── 초기 로드 ──────────────────────────────────────────────────────

    /// <summary>방 열람 시 1회 — 최신 페이지 로드 + 읽음 처리 + 멤버 후보(멘션) 준비.</summary>
    [RelayCommand]
    private async Task InitializeAsync()
    {
        await UiInvokeAsync(() => IsLoading = true).ConfigureAwait(false);

        IReadOnlyList<ChatMessage> page;
        try
        {
            page = await _realtime.LoadMessagesAsync(_room.Id, PageSize).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => { IsLoading = false; StatusMessage = Describe(ex); }).ConfigureAwait(false);
            return;
        }

        // 서버는 최신순으로 내려준다 — 표시는 오래된→최신이라 역순으로 채운다.
        var ordered = page.OrderBy(m => m.CreatedAt).ToList();
        await UiInvokeAsync(() =>
        {
            Messages.Clear();
            foreach (var dto in ordered)
                Messages.Add(new ChatMessageViewModel(dto, _currentUserId));
            HasMoreHistory = page.Count >= PageSize;
            IsLoading = false;
        }).ConfigureAwait(false);

        // 열람 시 읽음 처리(하단 도달과 동일 효과).
        await MarkReadAsync().ConfigureAwait(false);

        // 멘션 후보 준비(best-effort).
        try
        {
            var members = await _realtime.GetMembersAsync(_room.Id).ConfigureAwait(false);
            await UiInvokeAsync(() => Mentions.SetMembers(members, _currentUserId)).ConfigureAwait(false);
        }
        catch { /* graceful — 멘션 후보 없이도 동작. */ }
    }

    // ── 전송(낙관 렌더) ────────────────────────────────────────────────

    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft) || PendingAttachments.Count > 0;

    /// <summary>
    /// draft + pendingAttachments + mentions → realtime.SendMessageAsync. 전송 즉시 낙관 메시지를
    /// 추가(SendStatus=Pending)하고, 서버 echo(MessageUpserted)가 도착하면 id 매칭으로 치환한다(중복 방지).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var content = Draft?.Trim() ?? string.Empty;
        var attachments = PendingAttachments.ToList();
        if (string.IsNullOrEmpty(content) && attachments.Count == 0) return;

        var mentions = _draftMentions.Distinct(StringComparer.Ordinal).ToList();
        var localId = "local-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var optimistic = new ChatMessageViewModel(
            localId, _room.Id, _currentUserId, content, now, _currentUserId,
            mentions.Count > 0 ? mentions : null,
            attachments.Count > 0 ? attachments : null);

        await UiInvokeAsync(() =>
        {
            _pendingLocal[localId] = optimistic;
            Messages.Add(optimistic);

            // 입력/첨부/멘션 초기화.
            Draft = string.Empty;
            PendingAttachments.Clear();
            _draftMentions.Clear();
            Mentions.Close();
        }).ConfigureAwait(false);

        // typing stop(전송 시).
        await StopTypingAsync().ConfigureAwait(false);

        try
        {
            await _realtime.SendMessageAsync(
                _room.Id,
                string.IsNullOrEmpty(content) ? null : content,
                mentions.Count > 0 ? mentions : null,
                attachments.Count > 0 ? attachments : null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 전송 실패 — 낙관 메시지를 Failed로 표시(재시도 액션은 RetrySendCommand).
            await UiInvokeAsync(() =>
            {
                optimistic.SendStatus = ChatSendStatus.Failed;
                StatusMessage = Describe(ex);
            }).ConfigureAwait(false);
        }
    }

    private bool CanRetrySend(ChatMessageViewModel? m) => m is { SendStatus: ChatSendStatus.Failed };

    /// <summary>실패한 낙관 메시지 재전송. 동일 본문/첨부로 다시 시도.</summary>
    [RelayCommand(CanExecute = nameof(CanRetrySend))]
    private async Task RetrySendAsync(ChatMessageViewModel? message)
    {
        if (message is not { SendStatus: ChatSendStatus.Failed }) return;

        await UiInvokeAsync(() => message.SendStatus = ChatSendStatus.Pending).ConfigureAwait(false);
        try
        {
            var attachments = message.Attachments.ToList();
            await _realtime.SendMessageAsync(
                _room.Id,
                string.IsNullOrEmpty(message.Content) ? null : message.Content,
                null,
                attachments.Count > 0 ? attachments : null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() =>
            {
                message.SendStatus = ChatSendStatus.Failed;
                StatusMessage = Describe(ex);
            }).ConfigureAwait(false);
        }
    }

    // ── 수정 / 삭제 ────────────────────────────────────────────────────

    /// <summary>본인 메시지 수정. arg=(messageId, newContent).</summary>
    [RelayCommand]
    private async Task EditAsync((string messageId, string content) arg)
    {
        if (string.IsNullOrWhiteSpace(arg.content)) return;
        try
        {
            await _realtime.EditMessageAsync(_room.Id, arg.messageId, arg.content.Trim()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = Describe(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>본인 메시지 삭제(소프트). 멱등.</summary>
    [RelayCommand]
    private async Task DeleteAsync(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        try
        {
            await _realtime.DeleteMessageAsync(_room.Id, messageId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = Describe(ex)).ConfigureAwait(false);
        }
    }

    // ── 무한스크롤(before) ─────────────────────────────────────────────

    private bool CanLoadMore() => HasMoreHistory && !IsLoadingMore;

    /// <summary>상단 근접 시(View code-behind 트리거) 과거 이력 prepend. before=가장 오래된 메시지 id.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (!HasMoreHistory || IsLoadingMore) return;
        var oldest = Messages.FirstOrDefault();
        if (oldest is null) return;

        await UiInvokeAsync(() => IsLoadingMore = true).ConfigureAwait(false);

        IReadOnlyList<ChatMessage> page;
        try
        {
            page = await _realtime.LoadMessagesAsync(_room.Id, PageSize, before: oldest.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => { IsLoadingMore = false; StatusMessage = Describe(ex); }).ConfigureAwait(false);
            return;
        }

        var existingIds = new HashSet<string>(Messages.Select(m => m.Id), StringComparer.Ordinal);
        var older = page
            .Where(m => !existingIds.Contains(m.Id))
            .OrderBy(m => m.CreatedAt)
            .ToList();

        await UiInvokeAsync(() =>
        {
            // 오래된 메시지를 컬렉션 맨 앞에 역순 삽입(시간 오름차순 유지).
            for (var i = older.Count - 1; i >= 0; i--)
                Messages.Insert(0, new ChatMessageViewModel(older[i], _currentUserId));
            HasMoreHistory = page.Count >= PageSize;
            IsLoadingMore = false;
        }).ConfigureAwait(false);
    }

    // ── 읽음 ───────────────────────────────────────────────────────────

    /// <summary>방 열람/하단 도달 시 호출 — 서버에 읽음 처리. (View가 하단 스크롤 시 호출.)</summary>
    [RelayCommand]
    private async Task MarkReadAsync()
    {
        try
        {
            await _realtime.MarkReadAsync(_room.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = Describe(ex)).ConfigureAwait(false);
        }
    }

    // ── 첨부 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 컴포저 "+" 버튼 — 파일 선택은 View(OpenFileDialog)가 담당하므로 여기선 게이트만.
    /// 선택된 경로는 <see cref="UploadAndAttachAsync"/>(public)로 들어온다.
    /// </summary>
    [RelayCommand]
    private void AttachFile()
    {
        // No-op: 다이얼로그는 View 소유(MVVM-safe). UploadAndAttachAsync로 후속 처리.
    }

    /// <summary>View가 선택한 파일 경로를 업로드 → PendingAttachments에 칩 추가. public 진입점.</summary>
    public async Task UploadAndAttachAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            var attachment = await _realtime.UploadAttachmentAsync(filePath).ConfigureAwait(false);
            await UiInvokeAsync(() =>
            {
                PendingAttachments.Add(attachment);
                SendCommand.NotifyCanExecuteChanged();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => StatusMessage = Describe(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>첨부 다운로드(바이너리 스트림). View(저장 다이얼로그)가 호출하는 public 진입점 — realtime 위임.</summary>
    public Task<System.IO.Stream> DownloadAttachmentAsync(string attachmentId)
        => _realtime.DownloadAttachmentAsync(attachmentId);

    /// <summary>전송 전 첨부 칩 제거.</summary>
    [RelayCommand]
    private void RemoveAttachment(ChatAttachment? attachment)
    {
        if (attachment is null) return;
        PendingAttachments.Remove(attachment);
        SendCommand.NotifyCanExecuteChanged();
    }

    // ── typing 디바운스 ────────────────────────────────────────────────

    partial void OnDraftChanged(string value)
    {
        // 멘션 자동완성 — caret을 모르면 끝으로 가정(View가 caret 전달 시 NotifyDraftChanged 사용).
        Mentions.UpdateFromDraft(value ?? string.Empty, (value ?? string.Empty).Length);

        if (string.IsNullOrEmpty(value))
        {
            // 입력 비움 → 즉시 stop.
            _ = StopTypingAsync();
            return;
        }

        // 첫 입력에서 start 전송(쓰로틀: 이미 active면 재전송 안함).
        if (!_typingActive)
        {
            _typingActive = true;
            _ = _realtime.SendTypingAsync(_room.Id, TypingState.Start);
        }

        // 무입력 타이머 리셋.
        _typingTimer.Stop();
        _typingTimer.Start();
    }

    /// <summary>View가 caret 위치를 알 때 호출(멘션 정확도 향상). Draft setter 대체 경로.</summary>
    public void NotifyDraftChanged(string draft, int caretIndex)
    {
        Mentions.UpdateFromDraft(draft ?? string.Empty, caretIndex);
    }

    private void OnTypingTimerElapsed(object? sender, ElapsedEventArgs e) => _ = StopTypingAsync();

    private async Task StopTypingAsync()
    {
        _typingTimer.Stop();
        if (!_typingActive) return;
        _typingActive = false;
        try { await _realtime.SendTypingAsync(_room.Id, TypingState.Stop).ConfigureAwait(false); }
        catch { /* typing은 휘발성 — 실패 무시. */ }
    }

    private void OnMentionSelected(object? sender, MentionCandidate candidate)
    {
        // Draft 끝에 "@name " 삽입 + mentions 누적(간이 — View가 caret 기반 삽입을 더 정교히 할 수 있음).
        if (!_draftMentions.Contains(candidate.MemberId, StringComparer.Ordinal))
            _draftMentions.Add(candidate.MemberId);

        var insert = "@" + candidate.DisplayName + " ";
        Draft = (Draft ?? string.Empty).TrimEnd() is { Length: > 0 } existing
            ? existing + " " + insert
            : insert;
    }

    // ── realtime 이벤트(UI 마샬) ───────────────────────────────────────

    private void OnMessageUpserted(object? sender, RoomMessageEvent e)
    {
        if (!string.Equals(e.Message.RoomId, _room.Id, StringComparison.Ordinal)) return;

        _ = UiInvokeAsync(() =>
        {
            // 1) 서버 id로 이미 존재하면 갱신만(에코 dedup).
            var existing = Messages.FirstOrDefault(m => string.Equals(m.Id, e.Message.Id, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (e.IsDeleted) existing.ApplyUpdate(e.Message, isDeleted: true);
                else if (e.IsEdited) existing.ApplyUpdate(e.Message, isDeleted: false);
                else existing.ReplaceWithServer(e.Message);
                return;
            }

            // 2) 내 낙관 메시지의 echo면 치환(중복 방지). 가장 오래된 Pending 매칭.
            if (string.Equals(e.Message.SenderId, _currentUserId, StringComparison.Ordinal) && !e.IsEdited && !e.IsDeleted)
            {
                var pending = _pendingLocal.Values
                    .FirstOrDefault(p => p.SendStatus == ChatSendStatus.Pending
                                         && string.Equals(p.Content, e.Message.Content ?? string.Empty, StringComparison.Ordinal));
                if (pending is null)
                    pending = _pendingLocal.Values.FirstOrDefault(p => p.SendStatus == ChatSendStatus.Pending);

                if (pending is not null)
                {
                    var localKey = _pendingLocal.FirstOrDefault(kv => ReferenceEquals(kv.Value, pending)).Key;
                    if (localKey is not null) _pendingLocal.Remove(localKey);
                    pending.ReplaceWithServer(e.Message);
                    return;
                }
            }

            // 3) 신규 메시지 append(삭제/수정 이벤트인데 대상 부재 시 무시).
            if (!e.IsDeleted && !e.IsEdited)
                Messages.Add(new ChatMessageViewModel(e.Message, _currentUserId));
        });
    }

    private void OnReadChanged(object? sender, WsReadPayload p)
    {
        if (!string.Equals(p.RoomId, _room.Id, StringComparison.Ordinal)) return;
        if (string.Equals(p.MemberId, _currentUserId, StringComparison.Ordinal)) return;   // 내 읽음은 표시 대상 아님

        _ = UiInvokeAsync(() =>
        {
            // 내 메시지 중 상대 last_read_at 이하 생성분의 ReadByCount를 1 증가(간이 집계).
            foreach (var m in Messages.Where(m => m.IsMine && m.CreatedAt <= p.LastReadAt))
                m.ReadByCount = Math.Max(m.ReadByCount, 1);
        });
    }

    private void OnTypingChanged(object? sender, WsTypingPayload p)
    {
        if (!string.Equals(p.RoomId, _room.Id, StringComparison.Ordinal)) return;
        if (string.Equals(p.MemberId, _currentUserId, StringComparison.Ordinal)) return;

        _ = UiInvokeAsync(() =>
        {
            if (p.State == TypingState.Start)
            {
                if (!TypingMembers.Contains(p.MemberId)) TypingMembers.Add(p.MemberId);
            }
            else
            {
                TypingMembers.Remove(p.MemberId);
            }
        });
    }

    private void OnPresenceChanged(object? sender, WsPresencePayload p)
    {
        _ = UiInvokeAsync(() =>
        {
            if (p.Online)
            {
                if (!OnlinePresence.Contains(p.MemberId)) OnlinePresence.Add(p.MemberId);
            }
            else
            {
                OnlinePresence.Remove(p.MemberId);
            }
        });
    }

    private void UpdateTypingIndicator()
    {
        TypingIndicatorText = TypingMembers.Count switch
        {
            0 => string.Empty,
            1 => "입력 중...",
            _ => $"{TypingMembers.Count}명이 입력 중...",
        };
    }

    private static string Describe(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? "요청을 처리하지 못했습니다." : ex.Message;

    private static Task UiInvokeAsync(Action action) => UiDispatch.InvokeAsync(action);

    public void Dispose()
    {
        _unsubscribe();
        _typingTimer.Elapsed -= OnTypingTimerElapsed;
        _typingTimer.Dispose();
        Mentions.MemberSelected -= OnMentionSelected;
        Members.Dispose();
    }
}
