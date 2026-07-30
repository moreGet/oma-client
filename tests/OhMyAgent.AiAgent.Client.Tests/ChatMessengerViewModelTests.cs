using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.Services.Chat;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 메신저 VM 회귀 테스트. 여기 담긴 항목은 전부 실제로 화면에 드러났던 결함들이다 —
/// 말풍선의 UUID 노출, 멘션 토큰 잔류, 에코 유실 시 메시지 증발, 미리보기 공백, "읽음 0".
/// (테스트 프로세스에는 Application.Current 가 없어 UiDispatch 가 인라인 실행된다 → 마샬 없이 동기 검증 가능.)
/// </summary>
public class ChatMessengerViewModelTests
{
    private const string Me = "u-me";
    private const string Kim = "u-kim";
    private const string Lee = "u-lee";

    private static ChatMessage Msg(string id, string roomId, string sender, string content, long at,
        IReadOnlyList<string>? mentions = null, IReadOnlyList<ChatAttachment>? attachments = null, bool deleted = false)
        => new(id, roomId, sender, content, at, null, deleted, mentions, attachments);

    private static (FakeChatRealtime Realtime, ChatRoomViewModel Vm) NewRoom(params ChatMessage[] history)
    {
        var realtime = new FakeChatRealtime();
        realtime.Names[Me] = "나";
        realtime.Names[Kim] = "김철수";
        realtime.Names[Lee] = "이영희";
        realtime.Members = new[] { Me, Kim, Lee };
        realtime.Messages = history;

        var room = new ChatRoom("r1", ChatRoomType.Group, "개발팀", 1000, 0);
        return (realtime, new ChatRoomViewModel(realtime, room, new ChatIdentity(Me)));
    }

    // ── 보낸이 이름 ────────────────────────────────────────────────

    // 이걸 놓치면 그룹방 말풍선에 member UUID 가 그대로 노출된다.
    [Fact]
    public async Task IncomingBubble_ShowsResolvedSenderName_NotUuid()
    {
        var (_, vm) = NewRoom(Msg("m1", "r1", Kim, "안녕하세요", 1100));
        await vm.InitializeCommand.ExecuteAsync(null);

        var bubble = Assert.Single(vm.Messages, m => !m.IsMine);
        Assert.Equal("김철수", bubble.SenderName);
    }

    [Fact]
    public async Task DirectoryUpdate_ReresolvesExistingBubbleNames()
    {
        var realtime = new FakeChatRealtime { Members = new[] { Me, Kim } };
        realtime.Messages = new[] { Msg("m1", "r1", Kim, "hi", 1100) };
        var vm = new ChatRoomViewModel(realtime, new ChatRoom("r1", ChatRoomType.Group, "방", 0, 0), new ChatIdentity(Me));
        await vm.InitializeCommand.ExecuteAsync(null);

        Assert.Equal(Kim, vm.Messages[0].SenderName);   // 아직 이름 캐시 없음 → UUID 폴백

        realtime.Names[Kim] = "김철수";
        realtime.RaiseDirectoryUpdated();

        Assert.Equal("김철수", vm.Messages[0].SenderName);
    }

    // ── 읽음 집계 ──────────────────────────────────────────────────

    [Fact]
    public async Task ReadCount_CountsMembersWhoReadPastMessageTime()
    {
        var (realtime, vm) = NewRoom(Msg("m1", "r1", Me, "내 메시지", 1200));
        await vm.InitializeCommand.ExecuteAsync(null);
        var mine = vm.Messages.Single();

        // 메시지 시각 이전까지만 읽은 사람은 세지 않는다("읽음 0" 이 아니라 배지 자체가 안 뜬다).
        realtime.RaiseRead(new WsReadPayload("r1", Kim, 1150));
        Assert.Equal(0, mine.ReadByCount);
        Assert.False(mine.HasReads);

        realtime.RaiseRead(new WsReadPayload("r1", Kim, 1250));
        realtime.RaiseRead(new WsReadPayload("r1", Lee, 1250));
        Assert.Equal(2, mine.ReadByCount);   // 그룹방에서 1 로 포화되지 않아야 한다
        Assert.True(mine.HasReads);
    }

    [Fact]
    public async Task ReadCount_IgnoresBackwardReadPointer()
    {
        var (realtime, vm) = NewRoom(Msg("m1", "r1", Me, "내 메시지", 1200));
        await vm.InitializeCommand.ExecuteAsync(null);
        var mine = vm.Messages.Single();

        realtime.RaiseRead(new WsReadPayload("r1", Kim, 1300));
        realtime.RaiseRead(new WsReadPayload("r1", Kim, 1000));   // 후진 — 무시

        Assert.Equal(1, mine.ReadByCount);
    }

    [Fact]
    public async Task ReadCount_SeededFromServerOnRoomOpen()
    {
        var (realtime, vm) = NewRoom(Msg("m1", "r1", Me, "내 메시지", 1200));
        realtime.Reads = new[] { new ChatReadReceipt(Kim, 1300) };

        await vm.InitializeCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Messages.Single().ReadByCount);
    }

    // ── 멘션 자동완성 ──────────────────────────────────────────────

    // 끝에 덧붙이면 입력하던 "@이" 가 남아 "@이 @이영희 " 가 된다.
    [Fact]
    public async Task MentionCommit_ReplacesTypedToken()
    {
        var (_, vm) = NewRoom();
        await vm.InitializeCommand.ExecuteAsync(null);

        vm.Draft = "안녕 @이";
        vm.NotifyDraftChanged("안녕 @이", "안녕 @이".Length);
        Assert.True(vm.Mentions.IsActive);
        Assert.True(vm.Mentions.CommitSelection());

        Assert.Equal("안녕 @이영희 ", vm.Draft);
        Assert.False(vm.Mentions.IsActive);
    }

    [Fact]
    public async Task MentionCommit_MidSentence_UsesCaretAndAvoidsDoubleSpace()
    {
        var (_, vm) = NewRoom();
        await vm.InitializeCommand.ExecuteAsync(null);

        vm.Draft = "@김 님 확인 부탁";
        vm.NotifyDraftChanged("@김 님 확인 부탁", caretIndex: 2);
        Assert.True(vm.Mentions.IsActive);
        vm.Mentions.CommitSelection();

        Assert.Equal("@김철수 님 확인 부탁", vm.Draft);
    }

    // Popup(StaysOpen=False)이 스스로 닫아 IsActive 를 false 로 되돌린 뒤에도 다시 열려야 한다.
    [Fact]
    public void MentionPopup_ReopensAfterExternalClose()
    {
        var mentions = new MentionAutoCompleteViewModel();
        mentions.SetMembers(new[] { Kim }, Me, id => id == Kim ? "김철수" : id);

        mentions.UpdateFromDraft("@김", 2);
        Assert.True(mentions.IsActive);

        mentions.IsActive = false;                 // Popup 이 바깥 클릭으로 닫힘
        mentions.UpdateFromDraft("@김철", 3);
        Assert.True(mentions.IsActive);
    }

    [Fact]
    public void MentionSelection_MovesAndWrapsWithArrowKeys()
    {
        var mentions = new MentionAutoCompleteViewModel();
        mentions.SetMembers(new[] { Kim, Lee }, Me, id => id == Kim ? "김A" : "김B");

        mentions.UpdateFromDraft("@김", 2);
        Assert.Equal(2, mentions.Candidates.Count);
        Assert.Equal(0, mentions.SelectedIndex);

        mentions.MoveSelection(1);
        Assert.Equal(1, mentions.SelectedIndex);
        Assert.True(mentions.Candidates[1].IsSelected);

        mentions.MoveSelection(1);               // 순환
        Assert.Equal(0, mentions.SelectedIndex);
    }

    [Fact]
    public void MentionToken_NotTriggeredInsideEmailLikeText()
    {
        var mentions = new MentionAutoCompleteViewModel();
        mentions.SetMembers(new[] { Kim }, Me, _ => "김철수");

        mentions.UpdateFromDraft("me@김", 4);
        Assert.False(mentions.IsActive);
    }

    // ── 낙관 전송 / 에코 매칭 ──────────────────────────────────────

    // 에코가 유실된 옛 pending 을 새 메시지가 덮어쓰면 앞 메시지가 화면에서 증발한다.
    [Fact]
    public async Task Echo_MatchesByContent_DoesNotClobberUnrelatedPending()
    {
        var (realtime, vm) = NewRoom();
        await vm.InitializeCommand.ExecuteAsync(null);

        vm.Draft = "첫번째";
        await vm.SendCommand.ExecuteAsync(null);
        vm.Draft = "두번째";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Messages.Count);

        realtime.RaiseMessage(Msg("s2", "r1", Me, "두번째", 2000));   // 첫번째 에코는 끝내 안 옴

        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal("첫번째", vm.Messages[0].Content);
        Assert.Equal(ChatSendStatus.Pending, vm.Messages[0].SendStatus);
        Assert.Equal("s2", vm.Messages[1].Id);
        Assert.Equal(ChatSendStatus.Sent, vm.Messages[1].SendStatus);
    }

    [Fact]
    public async Task Echo_ReplacesMatchingOptimisticMessage()
    {
        var (realtime, vm) = NewRoom();
        await vm.InitializeCommand.ExecuteAsync(null);

        vm.Draft = "안녕";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.StartsWith("local-", vm.Messages.Single().Id);

        realtime.RaiseMessage(Msg("srv-1", "r1", Me, "안녕", 2000));

        var only = Assert.Single(vm.Messages);
        Assert.Equal("srv-1", only.Id);
        Assert.Equal(ChatSendStatus.Sent, only.SendStatus);
    }

    [Fact]
    public async Task SendFailure_MarksFailed_AndRetryResendsWithMentions()
    {
        var (realtime, vm) = NewRoom();
        await vm.InitializeCommand.ExecuteAsync(null);

        vm.Draft = "안녕 @이";
        vm.NotifyDraftChanged("안녕 @이", "안녕 @이".Length);
        vm.Mentions.CommitSelection();

        realtime.FailNextSend = true;
        await vm.SendCommand.ExecuteAsync(null);

        var failed = Assert.Single(vm.Messages);
        Assert.Equal(ChatSendStatus.Failed, failed.SendStatus);

        realtime.SentMentions = null;
        await vm.RetrySendCommand.ExecuteAsync(failed);

        Assert.Equal(ChatSendStatus.Pending, failed.SendStatus);
        Assert.NotNull(realtime.SentMentions);                       // 재전송에서 멘션이 유실되면 안 된다
        Assert.Contains(Lee, realtime.SentMentions!);
    }

    // ── 방 목록 ────────────────────────────────────────────────────

    [Fact]
    public async Task RoomList_UpdatesPreviewAndReordersOnNewMessage()
    {
        var realtime = new FakeChatRealtime
        {
            Rooms = new[]
            {
                new ChatRoom("a", ChatRoomType.Group, "A", 300, 0),
                new ChatRoom("b", ChatRoomType.Group, "B", 200, 0),
                new ChatRoom("c", ChatRoomType.Group, "C", 100, 0),
            },
        };
        var vm = new ChatRoomsViewModel(realtime);
        await vm.LoadRoomsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "a", "b", "c" }, vm.Rooms.Select(r => r.Id));

        realtime.RaiseMessage(Msg("x", "c", Kim, "야근\n하시나요", 900));

        Assert.Equal(new[] { "c", "a", "b" }, vm.Rooms.Select(r => r.Id));
        Assert.Equal("야근 하시나요", vm.Rooms[0].LastPreview);   // 미리보기가 빈 줄로 남지 않는다
        Assert.Equal(900, vm.Rooms[0].LastActivityAt);
    }

    [Fact]
    public async Task RoomList_PreviewShowsPlaceholders_ForAttachmentAndDeleted()
    {
        var realtime = new FakeChatRealtime { Rooms = new[] { new ChatRoom("a", ChatRoomType.Group, "A", 1, 0) } };
        var vm = new ChatRoomsViewModel(realtime);
        await vm.LoadRoomsCommand.ExecuteAsync(null);

        realtime.RaiseMessage(Msg("f", "a", Kim, "", 10,
            attachments: new[] { new ChatAttachment("id", "보고서.xlsx", "application/octet-stream", 1, "/u") }));
        Assert.Equal("파일을 보냈습니다", vm.Rooms[0].LastPreview);

        realtime.RaiseMessageEvent(new RoomMessageEvent(Msg("f", "a", Kim, "", 11), IsEdited: false, IsDeleted: true));
        Assert.Equal("삭제된 메시지", vm.Rooms[0].LastPreview);
    }

    // 셸이 이미 연 방을 목록에서 하이라이트할 때 방이 한 번 더 열리면 VM 이 중복 생성된다.
    [Fact]
    public async Task SelectWithoutOpening_HighlightsButDoesNotReopen()
    {
        var realtime = new FakeChatRealtime
        {
            Rooms = new[]
            {
                new ChatRoom("a", ChatRoomType.Group, "A", 2, 0),
                new ChatRoom("b", ChatRoomType.Group, "B", 1, 0),
            },
        };
        var vm = new ChatRoomsViewModel(realtime);
        await vm.LoadRoomsCommand.ExecuteAsync(null);

        var opens = 0;
        vm.RoomOpenRequested += (_, _) => opens++;

        vm.SelectWithoutOpening("b");
        Assert.Equal(0, opens);
        Assert.Equal("b", vm.SelectedRoom?.Id);

        vm.SelectedRoom = vm.Rooms.First(r => r.Id == "a");   // 사용자 클릭 경로는 그대로 열려야 한다
        Assert.Equal(1, opens);
    }

    // ── 라이브(localhost:8080) 테스트로 드러난 결함들 ───────────────

    // 세션 중 만들어진 1:1 방은 RoomUpserted 로 먼저 들어오고, 이후 목록 재로드는 그것을 "기존 행"으로
    // 분류해 건너뛴다 → 상대 이름이 영영 "1:1 대화" 로 남았다.
    [Fact]
    public async Task DirectRoom_CreatedDuringSession_ResolvesCounterpartName()
    {
        var realtime = new FakeChatRealtime();
        realtime.Names[Kim] = "김철수";
        realtime.Members = new[] { Me, Kim };
        realtime.MyId = Me;

        var vm = new ChatRoomsViewModel(realtime);
        await vm.LoadRoomsCommand.ExecuteAsync(null);

        // 방 생성 → RoomUpserted 로 목록에 먼저 등장(이름 미해석 상태)
        var direct = new ChatRoom("d1", ChatRoomType.Direct, null, 10, 0);
        realtime.Rooms = new[] { direct };
        realtime.RaiseRoomUpserted(direct);

        var item = Assert.Single(vm.Rooms);
        Assert.Equal("김철수", item.DisplayName);
        Assert.Equal(Kim, item.CounterpartId);
    }

    [Fact]
    public async Task DirectRoom_AlreadyInList_StillResolvedOnReload()
    {
        var realtime = new FakeChatRealtime();
        realtime.Members = new[] { Me, Kim };
        realtime.MyId = Me;
        realtime.Rooms = new[] { new ChatRoom("d1", ChatRoomType.Direct, null, 10, 0) };

        var vm = new ChatRoomsViewModel(realtime);
        realtime.CounterpartFails = true;                  // 1차 로드에서 상대 해석 실패
        await vm.LoadRoomsCommand.ExecuteAsync(null);
        Assert.Equal("1:1 대화", vm.Rooms[0].DisplayName);

        realtime.CounterpartFails = false;
        realtime.Names[Kim] = "김철수";
        await vm.LoadRoomsCommand.ExecuteAsync(null);       // 재로드 — 기존 행이라도 미해석이면 다시 시도해야 한다

        Assert.Equal("김철수", vm.Rooms[0].DisplayName);
    }

    // 비admin 은 /members 디렉터리가 403 이라 피드 카드에 UUID 앞자리가 그대로 떴다.
    [Fact]
    public async Task MentionFeed_ReresolvesSenderNameWhenDirectoryArrivesLater()
    {
        var realtime = new FakeChatRealtime();
        realtime.Messages = new[] { Msg("m1", "r1", Kim, "@나 확인 부탁", 100) };

        var feed = new MentionFeedViewModel(realtime, new ChatIdentity(Me));
        await feed.LoadCommand.ExecuteAsync(null);
        Assert.Equal(Kim, feed.Items[0].SenderName);        // 아직 캐시 미스 → UUID 폴백

        realtime.Names[Kim] = "김철수";
        realtime.RaiseDirectoryUpdated();

        Assert.Equal("김철수", feed.Items[0].SenderName);
        feed.Dispose();
    }

    // 피드를 열었을 때 이름을 모르면 그 방의 멤버를 조회해 캐시를 채워야 한다(방당 1회).
    [Fact]
    public async Task MentionFeed_PrimesNamesFromRoomMembers()
    {
        var realtime = new FakeChatRealtime();
        realtime.Members = new[] { Me, Kim };
        realtime.Messages = new[]
        {
            Msg("m1", "r1", Kim, "첫 멘션", 100),
            Msg("m2", "r1", Kim, "같은 방 두번째", 200),
        };
        // 실서비스(ChatRealtimeService.GetMembersAsync)는 ?detail=1 응답으로 이름 캐시를 채우고
        // 변경이 있으면 DirectoryUpdated 를 발화한다 — 그 계약을 그대로 흉내낸다.
        realtime.OnGetMembers = _ =>
        {
            realtime.Names[Kim] = "김철수";
            realtime.RaiseDirectoryUpdated();
        };

        var feed = new MentionFeedViewModel(realtime, new ChatIdentity(Me));
        await feed.LoadCommand.ExecuteAsync(null);

        Assert.Equal(1, realtime.GetMembersCalls);          // 같은 방이므로 1회만
        Assert.All(feed.Items, i => Assert.Equal("김철수", i.SenderName));
        feed.Dispose();
    }

    // ── 가짜 realtime ──────────────────────────────────────────────

    private sealed class FakeChatRealtime : IChatRealtimeService
    {
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> Members { get; set; } = Array.Empty<string>();
        public IReadOnlyList<ChatMessage> Messages { get; set; } = Array.Empty<ChatMessage>();
        public IReadOnlyList<ChatRoom> Rooms { get; set; } = Array.Empty<ChatRoom>();
        public IReadOnlyList<ChatReadReceipt> Reads { get; set; } = Array.Empty<ChatReadReceipt>();

        public bool FailNextSend { get; set; }
        public IReadOnlyList<string>? SentMentions { get; set; }

        public string MyId { get; set; } = Me;
        public bool CounterpartFails { get; set; }
        public int GetMembersCalls { get; private set; }
        public Action<string>? OnGetMembers { get; set; }

        public ChatConnectionState ConnectionState => ChatConnectionState.Connected;

        public event EventHandler<ChatConnectionState>? ConnectionStateChanged;
        public event EventHandler? AuthExpired;
        public event EventHandler<ChatOperationError>? OperationFailed;
        public event EventHandler<ChatRoom>? RoomUpserted;
        public event EventHandler<ChatUnreadResponse>? UnreadChanged;
        public event EventHandler<RoomMessageEvent>? MessageUpserted;
        public event EventHandler<WsReadPayload>? ReadChanged;
        public event EventHandler<WsTypingPayload>? TypingChanged;
        public event EventHandler<WsPresencePayload>? PresenceChanged;
        public event EventHandler<WsMemberPayload>? MemberJoined;
        public event EventHandler<WsMemberPayload>? MemberLeft;
        public event EventHandler? DirectoryUpdated;

        public void RaiseRead(WsReadPayload p) => ReadChanged?.Invoke(this, p);
        public void RaiseRoomUpserted(ChatRoom r) => RoomUpserted?.Invoke(this, r);
        public void RaiseMessage(ChatMessage m) => MessageUpserted?.Invoke(this, new RoomMessageEvent(m, false, false));
        public void RaiseMessageEvent(RoomMessageEvent e) => MessageUpserted?.Invoke(this, e);
        public void RaiseDirectoryUpdated() => DirectoryUpdated?.Invoke(this, EventArgs.Empty);

        // 미사용 이벤트 경고(CS0067) 억제 — 인터페이스 계약상 존재해야 한다.
        private void TouchUnused()
        {
            ConnectionStateChanged?.Invoke(this, ChatConnectionState.Connected);
            AuthExpired?.Invoke(this, EventArgs.Empty);
            OperationFailed?.Invoke(this, new ChatOperationError(0, "", ""));
            RoomUpserted?.Invoke(this, new ChatRoom("", ChatRoomType.Group, null, 0, 0));
            UnreadChanged?.Invoke(this, new ChatUnreadResponse(0, new Dictionary<string, int>()));
            TypingChanged?.Invoke(this, new WsTypingPayload("", "", TypingState.Stop));
            PresenceChanged?.Invoke(this, new WsPresencePayload("", false));
            MemberJoined?.Invoke(this, new WsMemberPayload("", ""));
            MemberLeft?.Invoke(this, new WsMemberPayload("", ""));
        }

        public void SetActiveRoom(string? roomId) { }

        public string DisplayName(string memberId)
            => Names.TryGetValue(memberId ?? string.Empty, out var n) ? n : memberId ?? string.Empty;

        public Task<string?> GetDirectCounterpartAsync(string roomId, CancellationToken ct = default)
            => Task.FromResult(CounterpartFails
                ? null
                : Members.FirstOrDefault(m => !string.Equals(m, MyId, StringComparison.Ordinal)));

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<ChatRoom>> LoadRoomsAsync(CancellationToken ct = default) => Task.FromResult(Rooms);

        public Task<IReadOnlyList<ChatMessage>> LoadMessagesAsync(string roomId, int limit = 50, string? before = null, CancellationToken ct = default)
            => Task.FromResult(before is null ? Messages : (IReadOnlyList<ChatMessage>)Array.Empty<ChatMessage>());

        public Task<ChatRoom> OpenDirectRoomAsync(string userId, CancellationToken ct = default)
            => Task.FromResult(new ChatRoom("d-" + userId, ChatRoomType.Direct, null, 0, 0));

        public Task<ChatRoom> CreateGroupRoomAsync(string name, IReadOnlyList<string> memberIds, CancellationToken ct = default)
            => Task.FromResult(new ChatRoom("g", ChatRoomType.Group, name, 0, 0));

        public Task SendMessageAsync(string roomId, string? content, IReadOnlyList<string>? mentions = null,
            IReadOnlyList<ChatAttachment>? attachments = null, CancellationToken ct = default)
        {
            SentMentions = mentions;
            if (!FailNextSend) return Task.CompletedTask;
            FailNextSend = false;
            return Task.FromException(new ChatApiException(500, "boom", "전송 실패"));
        }

        public Task EditMessageAsync(string roomId, string messageId, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteMessageAsync(string roomId, string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendTypingAsync(string roomId, TypingState state, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkReadAsync(string roomId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ChatReadReceipt>> GetReadsAsync(string roomId, CancellationToken ct = default) => Task.FromResult(Reads);
        public Task<IReadOnlyList<string>> GetMembersAsync(string roomId, CancellationToken ct = default)
        {
            GetMembersCalls++;
            OnGetMembers?.Invoke(roomId);
            return Task.FromResult(Members);
        }
        public Task<IReadOnlyList<string>> AddMembersAsync(string roomId, IReadOnlyList<string> memberIds, CancellationToken ct = default) => Task.FromResult(Members);
        public Task KickMemberAsync(string roomId, string memberId, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveRoomAsync(string roomId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetPresenceAsync(string roomId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<ChatMessage>> GetMentionsAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult(Messages);

        public Task<ChatAttachment> UploadAttachmentAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult(new ChatAttachment("a", Path.GetFileName(filePath), "application/octet-stream", 1, "/a"));

        public Task<Stream> DownloadAttachmentAsync(string attachmentId, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
