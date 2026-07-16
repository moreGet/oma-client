using System;

namespace OhMyAgent.AiAgent.Client.Services.Chat;

/// <summary>
/// "지금 로그인한 사용자는 누구인가"의 단일 출처. 채팅 VM 트리 전체가 이 객체를 <b>참조로</b> 공유한다.
///
/// 왜 문자열이 아닌가: 종전에는 App 시작 시 JWT 에서 뽑은 member UUID 문자열을 VM 생성자로 넘겨
/// 자식 VM 들이 각자 복사본을 들고 있었다. 그래서 A 로그아웃 → B 로그인 후에도 판정 기준이 A 로 남아,
/// B 의 메시지가 상대 말풍선으로, A 의 메시지가 내 말풍선으로 좌우가 뒤바뀌었다(읽음 표시도 동일).
/// 참조를 공유하면 로그인 시 이 값 하나만 갱신하면 트리 전체가 따라온다.
///
/// IsMine 은 클라이언트 판정이다 — 서버 sender_id 와 이 값을 비교할 뿐이므로,
/// 이 값이 낡으면 화면이 곧바로 틀어진다.
/// </summary>
public sealed class ChatIdentity
{
    private volatile string _memberId = string.Empty;

    public ChatIdentity(string? memberId = null) => MemberId = memberId ?? string.Empty;

    /// <summary>현재 사용자의 member UUID(JWT sub). 알 수 없으면 빈 문자열.</summary>
    public string MemberId
    {
        get => _memberId;
        set => _memberId = value ?? string.Empty;
    }

    /// <summary>
    /// <paramref name="senderId"/> 가 나인지. 신원을 모르면(빈 값) 항상 false —
    /// 빈 문자열끼리 맞아떨어져 남의 메시지가 "내 것"으로 보이는 사고를 막는다.
    /// </summary>
    public bool IsMine(string? senderId)
        => !string.IsNullOrEmpty(_memberId) && string.Equals(senderId, _memberId, StringComparison.Ordinal);

    /// <summary>로그아웃 시 신원을 비운다. 다음 로그인이 새 값을 채울 때까지 IsMine 은 전부 false 다.</summary>
    public void Clear() => MemberId = string.Empty;
}
