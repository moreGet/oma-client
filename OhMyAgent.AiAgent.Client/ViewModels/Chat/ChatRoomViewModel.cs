using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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

    /// <summary>WS 전송 후 서버 에코를 기다리는 상한. 넘기면 Failed 로 내려 재시도 버튼을 띄운다.</summary>
    private const int PendingEchoTimeoutMs = 12_000;

    /// <summary>상태 메시지 자동 소거(ms). 일시적 429/5xx 문구가 화면에 영구히 남지 않도록.</summary>
    private const int StatusAutoClearMs = 6_000;

    private readonly IChatRealtimeService _realtime;
    private readonly ChatRoom _room;
    private readonly ChatIdentity _identity;
    private readonly Action _unsubscribe;

    /// <summary>이 방 VM 의 수명. Dispose 시 취소되어 지연 작업(에코 타임아웃/상태 소거)이 따라 죽는다.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    // typing 디바운스 — Draft 변경 시 start(쓰로틀) + 무입력 N초/전송 시 stop.
    private readonly Timer _typingTimer;
    private bool _typingActive;

    /// <summary>임시(낙관) 메시지를 서버 echo와 매칭하기 위한 clientLocalId → VM 매핑.</summary>
    private readonly Dictionary<string, ChatMessageViewModel> _pendingLocal = new(StringComparer.Ordinal);

    /// <summary>타 멤버 읽음 지점(memberId → last_read_at). 내 메시지의 읽음 수를 정확히 세는 근거.</summary>
    private readonly Dictionary<string, long> _othersRead = new(StringComparer.Ordinal);

    /// <summary>View 가 caret 위치를 알려주기 시작했는지. true 면 Draft setter 의 "끝 caret 가정" 갱신을 건너뛴다.</summary>
    private bool _caretDrivenByView;

    /// <summary>멘션 확정으로 Draft 를 프로그램이 바꾸는 중 — 자동완성 재계산을 막는다.</summary>
    private bool _applyingMention;

    /// <summary>상태 메시지 세대. 늦게 도착한 소거 작업이 최신 메시지를 지우지 않게 한다.</summary>
    private int _statusGeneration;

    /// <summary>방 식별/표시(헤더 바인딩).</summary>
    public string RoomId => _room.Id;

    /// <summary>헤더 표시이름. group=방이름, 1:1=상대 해석이름(초기 "1:1 대화", InitializeAsync 에서 갱신).</summary>
    [ObservableProperty] private string _displayName;

    /// <summary>1:1 상대 memberId(이름 재해석용).</summary>
    private string? _counterpartId;

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

    /// <summary>멘션 확정 후 caret 을 삽입 지점 뒤로 옮겨달라는 요청(View 가 TextBox.CaretIndex 설정).</summary>
    public event EventHandler<int>? CaretMoveRequested;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public ChatRoomViewModel(IChatRealtimeService realtime, ChatRoom room, ChatIdentity identity)
    {
        _realtime = realtime;
        _room = room;
        _identity = identity;
        _displayName = string.IsNullOrWhiteSpace(room.Name) ? "1:1 대화" : room.Name!;
        Members = new RoomMembersViewModel(realtime, room, identity);

        _typingTimer = new Timer(TypingDebounceMs) { AutoReset = false };
        _typingTimer.Elapsed += OnTypingTimerElapsed;

        Mentions.MemberSelected += OnMentionSelected;
        TypingMembers.CollectionChanged += (_, _) => UpdateTypingIndicator();

        _realtime.MessageUpserted += OnMessageUpserted;
        _realtime.ReadChanged += OnReadChanged;
        _realtime.TypingChanged += OnTypingChanged;
        _realtime.PresenceChanged += OnPresenceChanged;
        _realtime.DirectoryUpdated += OnDirectoryUpdated;
        _unsubscribe = () =>
        {
            _realtime.MessageUpserted -= OnMessageUpserted;
            _realtime.ReadChanged -= OnReadChanged;
            _realtime.TypingChanged -= OnTypingChanged;
            _realtime.PresenceChanged -= OnPresenceChanged;
            _realtime.DirectoryUpdated -= OnDirectoryUpdated;
        };
    }

    /// <summary>디렉터리 갱신 → 1:1 헤더 이름 + 이미 그려진 말풍선의 보낸이 이름을 재해석(UUID→이름).</summary>
    private void OnDirectoryUpdated(object? sender, EventArgs e)
        => _ = UiInvokeAsync(() =>
        {
            if (!string.IsNullOrEmpty(_counterpartId))
                DisplayName = _realtime.DisplayName(_counterpartId!);

            foreach (var m in Messages)
                if (!m.IsMine)
                    m.SenderName = _realtime.DisplayName(m.SenderId);
        });

    /// <summary>
    /// 서버 DTO → 말풍선 VM. 보낸이 이름을 여기서 해석한다 — 이걸 빼먹으면 그룹방 말풍선에
    /// member UUID 가 그대로 노출된다(ChatMessageViewModel 기본값이 SenderId 이므로).
    /// </summary>
    private ChatMessageViewModel CreateBubble(ChatMessage dto)
    {
        var vm = new ChatMessageViewModel(dto, _identity);
        if (!vm.IsMine)
            vm.SenderName = _realtime.DisplayName(dto.SenderId);
        else
            ApplyReadCount(vm);
        return vm;
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
            await UiInvokeAsync(() => { IsLoading = false; ShowStatus(Describe(ex)); }).ConfigureAwait(false);
            return;
        }

        // 서버는 최신순으로 내려준다 — 표시는 오래된→최신이라 역순으로 채운다.
        var ordered = page.OrderBy(m => m.CreatedAt).ToList();
        await UiInvokeAsync(() =>
        {
            Messages.Clear();
            foreach (var dto in ordered)
                Messages.Add(CreateBubble(dto));
            HasMoreHistory = page.Count >= PageSize;
            IsLoading = false;
        }).ConfigureAwait(false);

        // 열람 시 읽음 처리(읽을 게 있을 때만 서버 통지 — MarkReadAsync 내부 throttle).
        await MarkReadAsync().ConfigureAwait(false);

        // 멤버 + presence + 읽음지점을 1회씩만 조회해 멤버패널·멘션후보·온라인수·1:1 상대이름·읽음배지에 공유한다.
        try
        {
            var members = await _realtime.GetMembersAsync(_room.Id).ConfigureAwait(false);
            var online  = await _realtime.GetPresenceAsync(_room.Id).ConfigureAwait(false);
            var reads   = await _realtime.GetReadsAsync(_room.Id).ConfigureAwait(false);
            await UiInvokeAsync(() =>
            {
                Members.ApplyMembers(members, online);                          // 멤버 패널(Flyout)
                Mentions.SetMembers(members, _identity.MemberId, _realtime.DisplayName); // 멘션 후보
                OnlinePresence.Clear();                                          // 헤더 "N명 온라인"
                foreach (var id in online) OnlinePresence.Add(id);

                foreach (var r in reads)                                         // 읽음 배지 초기 시드
                    if (!_identity.IsMine(r.MemberId))
                        _othersRead[r.MemberId] = r.LastReadAt;
                RecomputeReadCounts();

                foreach (var m in Messages)                                      // 이름 캐시가 방금 채워졌으므로 재해석
                    if (!m.IsMine)
                        m.SenderName = _realtime.DisplayName(m.SenderId);

                if (_room.Type == ChatRoomType.Direct)                          // 1:1 헤더 = 상대 이름
                {
                    var counterpart = members.FirstOrDefault(m => !_identity.IsMine(m));
                    if (!string.IsNullOrEmpty(counterpart))
                    {
                        _counterpartId = counterpart;
                        DisplayName = _realtime.DisplayName(counterpart);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch { /* graceful — 멤버/멘션/온라인 표시 없이도 동작. */ }
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
            localId, _room.Id, _identity.MemberId, content, now, _identity,
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
            await UiInvokeAsync(() =>
            {
                MarkPendingFailed(optimistic, Describe(ex));
            }).ConfigureAwait(false);
            return;
        }

        // WS 로 나간 경우 응답이 없다 — 에코가 끝내 안 오면 영원히 Pending(흐린 말풍선)으로 남으므로
        // 상한을 두고 Failed 로 내려 재시도 버튼을 띄운다.
        _ = WatchPendingEchoAsync(optimistic);
    }

    private bool CanRetrySend(ChatMessageViewModel? m) => m is { SendStatus: ChatSendStatus.Failed };

    /// <summary>실패한 낙관 메시지 재전송. 동일 본문/첨부/멘션으로 다시 시도.</summary>
    [RelayCommand(CanExecute = nameof(CanRetrySend))]
    private async Task RetrySendAsync(ChatMessageViewModel? message)
    {
        if (message is not { SendStatus: ChatSendStatus.Failed }) return;

        await UiInvokeAsync(() =>
        {
            message.SendStatus = ChatSendStatus.Pending;
            _pendingLocal[message.Id] = message;   // 실패 시 제거됐던 항목을 에코 매칭 대상으로 되돌린다
        }).ConfigureAwait(false);

        try
        {
            var attachments = message.Attachments.ToList();
            var mentions = message.Mentions;
            await _realtime.SendMessageAsync(
                _room.Id,
                string.IsNullOrEmpty(message.Content) ? null : message.Content,
                mentions is { Count: > 0 } ? mentions : null,
                attachments.Count > 0 ? attachments : null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UiInvokeAsync(() => MarkPendingFailed(message, Describe(ex))).ConfigureAwait(false);
            return;
        }

        _ = WatchPendingEchoAsync(message);
    }

    /// <summary>실패 처리 — 상태 표시 + 에코 매칭 후보에서 제외(고아 pending 이 남의 에코를 가로채지 않게).</summary>
    private void MarkPendingFailed(ChatMessageViewModel message, string? status)
    {
        message.SendStatus = ChatSendStatus.Failed;
        _pendingLocal.Remove(message.Id);
        if (!string.IsNullOrEmpty(status)) ShowStatus(status!);
        RetrySendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>에코 대기 상한 감시. 시간 내 치환되면(=사전에서 사라지면) 아무것도 하지 않는다.</summary>
    private async Task WatchPendingEchoAsync(ChatMessageViewModel message)
    {
        try
        {
            await Task.Delay(PendingEchoTimeoutMs, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }   // 방 닫힘

        await UiInvokeAsync(() =>
        {
            if (message.SendStatus != ChatSendStatus.Pending) return;
            if (!_pendingLocal.TryGetValue(message.Id, out var current) || !ReferenceEquals(current, message)) return;
            MarkPendingFailed(message, "메시지 전송을 확인하지 못했습니다. 다시 시도하세요.");
        }).ConfigureAwait(false);
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
            await UiInvokeAsync(() => ShowStatus(Describe(ex))).ConfigureAwait(false);
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
            await UiInvokeAsync(() => ShowStatus(Describe(ex))).ConfigureAwait(false);
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
            await UiInvokeAsync(() => { IsLoadingMore = false; ShowStatus(Describe(ex)); }).ConfigureAwait(false);
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
                Messages.Insert(0, CreateBubble(older[i]));
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
            await UiInvokeAsync(() => ShowStatus(Describe(ex))).ConfigureAwait(false);
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
            await UiInvokeAsync(() => ShowStatus(Describe(ex))).ConfigureAwait(false);
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
        // 멘션 자동완성 — View 가 caret 을 알려주는 환경이면 그쪽(NotifyDraftChanged)에 맡긴다.
        // 여기서 "끝 caret" 가정으로 한 번 더 계산하면 키 입력마다 후보가 두 번 재구성돼 선택이 튄다.
        if (!_applyingMention && !_caretDrivenByView)
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

    /// <summary>View가 caret 위치를 알 때 호출(멘션 정확도). 이후 Draft setter 의 끝-caret 추정은 꺼진다.</summary>
    public void NotifyDraftChanged(string draft, int caretIndex)
    {
        _caretDrivenByView = true;
        if (_applyingMention) return;
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

    /// <summary>
    /// 후보 확정 — 입력 중이던 `@토큰` 구간을 선택한 이름으로 <b>치환</b>한다.
    /// 끝에 덧붙이면 "@홍" 을 치다 고른 순간 "@홍 @홍길동 " 이 되어 오타처럼 남는다.
    /// </summary>
    private void OnMentionSelected(object? sender, MentionCandidate candidate)
    {
        if (!_draftMentions.Contains(candidate.MemberId, StringComparer.Ordinal))
            _draftMentions.Add(candidate.MemberId);

        var draft = Draft ?? string.Empty;
        var start = Mentions.TokenStart;
        var end = Mentions.TokenEnd;

        string next;
        int caret;

        if (start >= 0 && start <= draft.Length && end >= start && end <= draft.Length)
        {
            // 뒤에 이미 공백이 있으면 구분 공백을 덧붙이지 않는다("@김철수  님" 처럼 두 칸이 되는 것 방지).
            var needsSpace = end >= draft.Length || !char.IsWhiteSpace(draft[end]);
            var insert = "@" + candidate.DisplayName + (needsSpace ? " " : string.Empty);

            next = draft[..start] + insert + draft[end..];
            caret = start + insert.Length;
        }
        else
        {
            // 토큰 구간을 모르면(직접 호출 등) 기존 동작대로 끝에 덧붙인다.
            var insert = "@" + candidate.DisplayName + " ";
            next = draft.Length == 0 ? insert : draft.TrimEnd() + " " + insert;
            caret = next.Length;
        }

        _applyingMention = true;
        try
        {
            Draft = next;
        }
        finally
        {
            _applyingMention = false;
        }

        CaretMoveRequested?.Invoke(this, caret);
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

            // 2) 내 낙관 메시지의 echo면 치환(중복 방지).
            //    본문이 일치하는 Pending 만 매칭한다 — "아무 Pending" 폴백은 에코가 유실된 옛 말풍선을
            //    새 메시지 내용으로 덮어써서 이전 메시지를 화면에서 증발시킨다.
            if (_identity.IsMine(e.Message.SenderId) && !e.IsEdited && !e.IsDeleted)
            {
                var incoming = e.Message.Content ?? string.Empty;
                foreach (var kv in _pendingLocal)
                {
                    if (kv.Value.SendStatus != ChatSendStatus.Pending) continue;
                    if (!string.Equals(kv.Value.Content, incoming, StringComparison.Ordinal)) continue;

                    _pendingLocal.Remove(kv.Key);
                    kv.Value.ReplaceWithServer(e.Message);
                    ApplyReadCount(kv.Value);
                    return;
                }
            }

            // 3) 신규 메시지 append(삭제/수정 이벤트인데 대상 부재 시 무시).
            if (!e.IsDeleted && !e.IsEdited)
                Messages.Add(CreateBubble(e.Message));
        });
    }

    private void OnReadChanged(object? sender, WsReadPayload p)
    {
        if (!string.Equals(p.RoomId, _room.Id, StringComparison.Ordinal)) return;
        if (_identity.IsMine(p.MemberId)) return;   // 내 읽음은 표시 대상 아님

        _ = UiInvokeAsync(() =>
        {
            // 단조성 — 후진 무시. 전진했을 때만 재계산한다.
            if (_othersRead.TryGetValue(p.MemberId, out var prev) && prev >= p.LastReadAt) return;
            _othersRead[p.MemberId] = p.LastReadAt;
            RecomputeReadCounts();
        });
    }

    /// <summary>내 메시지의 읽음 수 = 생성시각 이후를 읽은 타 멤버 수. UI 스레드에서 호출.</summary>
    private void RecomputeReadCounts()
    {
        if (_othersRead.Count == 0) return;
        foreach (var m in Messages)
            if (m.IsMine)
                ApplyReadCount(m);
    }

    private void ApplyReadCount(ChatMessageViewModel message)
    {
        if (!message.IsMine || _othersRead.Count == 0) return;
        var count = 0;
        foreach (var kv in _othersRead)
            if (kv.Value >= message.CreatedAt) count++;
        message.ReadByCount = count;
    }

    private void OnTypingChanged(object? sender, WsTypingPayload p)
    {
        if (!string.Equals(p.RoomId, _room.Id, StringComparison.Ordinal)) return;
        if (_identity.IsMine(p.MemberId)) return;

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

    /// <summary>상태 문구 표시 + 일정 시간 뒤 자동 소거(최신 메시지만 살아남는다). UI 스레드에서 호출.</summary>
    private void ShowStatus(string message)
    {
        StatusMessage = message;
        if (string.IsNullOrEmpty(message)) return;

        var generation = ++_statusGeneration;
        _ = ClearStatusLaterAsync(generation);
    }

    private async Task ClearStatusLaterAsync(int generation)
    {
        try { await Task.Delay(StatusAutoClearMs, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        await UiInvokeAsync(() =>
        {
            if (_statusGeneration == generation) StatusMessage = string.Empty;
        }).ConfigureAwait(false);
    }

    private static string Describe(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? "요청을 처리하지 못했습니다." : ex.Message;

    private static Task UiInvokeAsync(Action action) => UiDispatch.InvokeAsync(action);

    public void Dispose()
    {
        _unsubscribe();
        try { _lifetime.Cancel(); } catch { /* 이미 정리됨 */ }
        _lifetime.Dispose();
        _typingTimer.Elapsed -= OnTypingTimerElapsed;
        _typingTimer.Dispose();
        Mentions.MemberSelected -= OnMentionSelected;
        _pendingLocal.Clear();
        Members.Dispose();
    }
}
