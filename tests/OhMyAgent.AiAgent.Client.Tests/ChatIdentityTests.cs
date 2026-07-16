using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.Services.Chat;
using OhMyAgent.AiAgent.Client.ViewModels.Chat;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 채팅 신원. IsMine 은 서버 sender_id 와 "클라이언트가 아는 내 id"를 비교하는 클라 판정이라,
/// 이 값이 낡으면 좌우 말풍선과 읽음 표시가 통째로 뒤바뀐다.
/// </summary>
public class ChatIdentityTests
{
    private const string UserA = "aaaaaaaa-0000-0000-0000-000000000000";
    private const string UserB = "bbbbbbbb-1111-1111-1111-111111111111";

    private static ChatMessage Msg(string id, string senderId)
        => new(Id: id, RoomId: "room-1", SenderId: senderId, Content: "hi",
               CreatedAt: 1, EditedAt: null, Deleted: false, Mentions: null, Attachments: null);

    [Fact]
    public void IsMine_MatchesOwnId()
    {
        var identity = new ChatIdentity(UserA);

        Assert.True(identity.IsMine(UserA));
        Assert.False(identity.IsMine(UserB));
    }

    // 빈 신원일 때 빈 senderId 와 맞아떨어져 남의 메시지가 "내 것"이 되면 안 된다.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void IsMine_IsAlwaysFalseWhenIdentityUnknown(string? senderId)
    {
        var identity = new ChatIdentity("");

        Assert.False(identity.IsMine(senderId));
        Assert.False(identity.IsMine(UserA));
    }

    [Fact]
    public void Clear_MakesEverythingNotMine()
    {
        var identity = new ChatIdentity(UserA);
        identity.Clear();

        Assert.False(identity.IsMine(UserA));
        Assert.Equal(string.Empty, identity.MemberId);
    }

    [Fact]
    public void NullMemberId_IsNormalizedToEmpty()
    {
        Assert.Equal(string.Empty, new ChatIdentity(null).MemberId);
        Assert.Equal(string.Empty, new ChatIdentity(UserA) { MemberId = null! }.MemberId);
    }

    // ── 핵심 회귀: 재로그인 후 사용자 전환 ──
    //
    // 종전에는 App 시작 시 뽑은 문자열을 VM 이 복사해 들고 있어, A 로그아웃 → B 로그인 후에도
    // 판정 기준이 A 로 남았다. 참조 공유이므로 값 하나만 갱신하면 이후 생성되는 버블이 새 기준을 따른다.
    [Fact]
    public void UpdatingIdentity_AffectsMessagesCreatedAfterwards()
    {
        var identity = new ChatIdentity(UserA);

        var beforeSwitch = new ChatMessageViewModel(Msg("m1", UserA), identity);
        Assert.True(beforeSwitch.IsMine);

        // A 로그아웃 → B 로그인.
        identity.MemberId = UserB;

        var aMessage = new ChatMessageViewModel(Msg("m2", UserA), identity);
        var bMessage = new ChatMessageViewModel(Msg("m3", UserB), identity);

        Assert.False(aMessage.IsMine);   // A 의 메시지가 내 것으로 보이면 안 된다
        Assert.True(bMessage.IsMine);    // B 의 메시지가 내 것이어야 한다
    }

    [Fact]
    public void OptimisticMessage_UsesCurrentIdentity()
    {
        var identity = new ChatIdentity(UserA);
        identity.MemberId = UserB;

        // 낙관 렌더 경로(전송 직후, 서버 echo 전).
        var optimistic = new ChatMessageViewModel(
            clientLocalId: "local-1", roomId: "room-1", senderId: UserB,
            content: "hello", createdAt: 1, identity: identity);

        Assert.True(optimistic.IsMine);
        Assert.Equal(ChatSendStatus.Pending, optimistic.SendStatus);
    }

    [Fact]
    public void ClearedIdentity_MakesExistingUserMessagesNotMine()
    {
        var identity = new ChatIdentity(UserA);
        identity.Clear();

        // 로그아웃 직후 신원이 채워지기 전에 만들어지는 버블은 전부 "내 것 아님"이어야 한다.
        Assert.False(new ChatMessageViewModel(Msg("m1", UserA), identity).IsMine);
    }
}
