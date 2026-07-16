using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OhMyAgent.AiAgent.Client.Models.Chat;
using OhMyAgent.AiAgent.Client.Services.Chat;

namespace OhMyAgent.AiAgent.Client.ViewModels.Chat;

/// <summary>
/// 한 메시지 버블(§4.4). 내것/남것, edited/deleted, 멘션 세그먼트, 첨부 목록, 전송상태(낙관 렌더)를
/// 보유한다. 식별자/발신자/생성시각은 불변(읽기 전용), 그 외(내용·수정·삭제·전송상태·읽음수)는
/// <see cref="ObservableProperty"/>로 실시간 갱신한다.
/// </summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    /// <summary>서버 메시지 id. 낙관 렌더 시에는 임시 clientLocalId(전송 echo 도착 시 <see cref="ReplaceWithServer"/>로 치환).</summary>
    public string Id { get; private set; }

    public string SenderId { get; }

    /// <summary>발신자 표시이름(멘션 피드 등). 기본=SenderId, 상위(피드 VM)가 디렉터리로 해석해 설정.</summary>
    [ObservableProperty] private string _senderName;

    /// <summary>소속 방 id(DTO에 포함). 멘션 피드에서 "방 열기"에 사용. 낙관 메시지는 생성 시 전달된 방 id.</summary>
    public string RoomId { get; }

    /// <summary>본문. 삭제 시 서버가 빈 문자열로 내려주므로 IsDeleted와 함께 표시 결정.</summary>
    [ObservableProperty] private string _content;

    /// <summary>생성 시각(unix epoch 초). UI 표시 시 converter가 로컬 변환.</summary>
    public long CreatedAt { get; }

    /// <summary>수정 시각(unix epoch 초). null이 아니면 "수정됨" 표시.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private long? _editedAt;

    /// <summary>소프트 삭제 여부. true면 "삭제된 메시지" 표시(멘션/첨부 숨김).</summary>
    [ObservableProperty] private bool _isDeleted;

    /// <summary>내가 보낸 메시지 여부(senderId 가 현재 신원과 일치). 정렬/버블색 결정.</summary>
    public bool IsMine { get; }

    /// <summary>"수정됨" 배지 표시용 계산 프로퍼티(EditedAt != null &amp;&amp; !IsDeleted).</summary>
    public bool IsEdited => EditedAt is not null && !IsDeleted;

    /// <summary>@멘션 하이라이트를 위한 본문 분할(§4.4). IsMention 세그먼트만 강조색 Run.</summary>
    [ObservableProperty] private IReadOnlyList<MentionSegment> _segments;

    /// <summary>첨부 칩 목록(WS 실시간 수신분만 채워짐 — REST 이력은 null이라 비어 있음, §7).</summary>
    public ObservableCollection<ChatAttachment> Attachments { get; } = [];

    /// <summary>전송 상태(낙관: Pending → echo: Sent → 실패: Failed). 와이어 미전송 클라 전용.</summary>
    [ObservableProperty] private ChatSendStatus _sendStatus = ChatSendStatus.Sent;

    /// <summary>이 메시지를 읽은(내 메시지 기준 타 멤버) 수. ReadChanged 구독으로 갱신.</summary>
    [ObservableProperty] private int _readByCount;

    /// <summary>서버 DTO로부터 구성(이력/실시간 수신분). mentions/attachments는 null 가능(REST 이력 한계).</summary>
    public ChatMessageViewModel(ChatMessage dto, ChatIdentity identity)
    {
        Id = dto.Id;
        SenderId = dto.SenderId;
        _senderName = dto.SenderId;
        RoomId = dto.RoomId;
        _content = dto.Content ?? string.Empty;
        CreatedAt = dto.CreatedAt;
        _editedAt = dto.EditedAt;
        _isDeleted = dto.Deleted ?? false;
        IsMine = identity.IsMine(dto.SenderId);
        _segments = BuildSegments(_content, dto.Mentions);

        if (dto.Attachments is { Count: > 0 } atts)
            foreach (var a in atts)
                Attachments.Add(a);
    }

    /// <summary>낙관 렌더용 임시 메시지 구성(전송 직후, 서버 echo 전). SendStatus=Pending.</summary>
    public ChatMessageViewModel(
        string clientLocalId,
        string roomId,
        string senderId,
        string content,
        long createdAt,
        ChatIdentity identity,
        IReadOnlyList<string>? mentions = null,
        IReadOnlyList<ChatAttachment>? attachments = null)
    {
        Id = clientLocalId;
        SenderId = senderId;
        _senderName = senderId;
        RoomId = roomId;
        _content = content ?? string.Empty;
        CreatedAt = createdAt;
        _editedAt = null;
        _isDeleted = false;
        IsMine = identity.IsMine(senderId);
        _segments = BuildSegments(_content, mentions);
        _sendStatus = ChatSendStatus.Pending;

        if (attachments is { Count: > 0 } atts)
            foreach (var a in atts)
                Attachments.Add(a);
    }

    /// <summary>
    /// 낙관 메시지에 서버 echo가 도착하면 호출 — 임시 id를 서버 id로 치환하고 상태를 Sent로 올린다.
    /// 수정/삭제/첨부도 서버 DTO 기준으로 동기화(중복 추가 방지). UI 스레드에서 호출.
    /// </summary>
    public void ReplaceWithServer(ChatMessage dto)
    {
        Id = dto.Id;
        Content = dto.Content ?? string.Empty;
        EditedAt = dto.EditedAt;
        IsDeleted = dto.Deleted ?? false;
        Segments = BuildSegments(Content, dto.Mentions);
        SendStatus = ChatSendStatus.Sent;

        // 첨부는 실시간 수신분(WS)에만 존재 — 비어 있으면 그대로 둔다(낙관 첨부 유지).
        if (dto.Attachments is { Count: > 0 } atts)
        {
            Attachments.Clear();
            foreach (var a in atts)
                Attachments.Add(a);
        }
    }

    /// <summary>WS message_edited/message_deleted 수신 시 본문/플래그/세그먼트를 갱신(UI 스레드).</summary>
    public void ApplyUpdate(ChatMessage dto, bool isDeleted)
    {
        Content = dto.Content ?? string.Empty;
        EditedAt = dto.EditedAt;
        IsDeleted = isDeleted || (dto.Deleted ?? false);
        Segments = BuildSegments(Content, IsDeleted ? null : dto.Mentions);
    }

    /// <summary>
    /// 본문을 @멘션 경계로 분할한다. mentions는 memberId 목록(REST 이력은 null → 단일 일반 세그먼트).
    /// 멤버 표시명을 알 수 없으므로 "@" + 토큰(공백 전까지) 패턴을 멘션으로 표기(SSOT 임시 대응).
    /// </summary>
    private static IReadOnlyList<MentionSegment> BuildSegments(string content, IReadOnlyList<string>? mentions)
    {
        if (string.IsNullOrEmpty(content) || mentions is not { Count: > 0 })
            return [new MentionSegment(content ?? string.Empty, false)];

        var segments = new List<MentionSegment>();
        var i = 0;
        while (i < content.Length)
        {
            var at = content.IndexOf('@', i);
            if (at < 0)
            {
                segments.Add(new MentionSegment(content[i..], false));
                break;
            }

            if (at > i)
                segments.Add(new MentionSegment(content[i..at], false));

            // @ 뒤 토큰(공백/줄바꿈 전까지)을 멘션 세그먼트로.
            var end = at + 1;
            while (end < content.Length && !char.IsWhiteSpace(content[end]))
                end++;

            segments.Add(new MentionSegment(content[at..end], true));
            i = end;
        }

        return segments.Count > 0 ? segments : [new MentionSegment(content, false)];
    }
}

/// <summary>본문 분할 한 조각(§4.4). IsMention=true면 강조색 Run으로 렌더.</summary>
public sealed record MentionSegment(string Text, bool IsMention);
