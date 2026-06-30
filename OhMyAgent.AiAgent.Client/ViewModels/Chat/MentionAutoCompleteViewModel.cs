using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OhMyAgent.AiAgent.Client.ViewModels.Chat;

/// <summary>
/// `@` 입력 시 멤버 자동완성(§4.3). 방 멤버 후보를 보유하고, 현재 입력 토큰으로 필터한 후보를
/// Popup(IsActive)으로 노출한다. 항목 선택 시 <see cref="MemberSelected"/> 이벤트로 (memberId,
/// displayName)을 상위(ChatRoomViewModel)에 전달 — Draft 삽입/mentions 누적은 상위가 담당.
/// </summary>
public sealed partial class MentionAutoCompleteViewModel : ObservableObject
{
    /// <summary>Popup 에 노출할 최대 후보 수(과도한 목록 방지).</summary>
    private const int MaxCandidatesDisplayed = 8;

    /// <summary>방의 전체 멤버 후보(memberId). 표시명 미제공이라 memberId를 라벨로도 사용.</summary>
    private readonly List<MentionCandidate> _all = [];

    /// <summary>현재 입력 토큰으로 필터된 후보(Popup 리스트 바인딩).</summary>
    public ObservableCollection<MentionCandidate> Candidates { get; } = [];

    /// <summary>Popup 표시 여부(@ 입력 + 후보 존재 시 true).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>현재 필터 쿼리(@ 뒤 부분 토큰). 디버그/표시용.</summary>
    [ObservableProperty] private string _query = string.Empty;

    /// <summary>방 멤버 후보 갱신. <paramref name="resolveName"/> 로 memberId→표시이름 해석(없으면 id 라벨).</summary>
    public void SetMembers(IReadOnlyList<string> memberIds, string? currentUserId = null, Func<string, string>? resolveName = null)
    {
        _all.Clear();
        foreach (var id in memberIds)
        {
            // 나 자신은 멘션 후보에서 제외.
            if (currentUserId is not null && string.Equals(id, currentUserId, StringComparison.Ordinal))
                continue;
            var name = resolveName?.Invoke(id);
            _all.Add(new MentionCandidate(id, string.IsNullOrWhiteSpace(name) ? id : name!));
        }
    }

    /// <summary>
    /// Draft에 입력된 `@`토큰을 기준으로 후보를 필터하고 Popup을 연다. caret 직전 토큰이 `@`로
    /// 시작하지 않으면 닫는다(IsActive=false). 호출자는 Draft setter에서 caret 위치를 넘긴다.
    /// </summary>
    public void UpdateFromDraft(string draft, int caretIndex)
    {
        var token = ExtractMentionToken(draft, caretIndex);
        if (token is null)
        {
            Close();
            return;
        }

        Query = token;
        var filtered = _all
            .Where(c => c.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Take(MaxCandidatesDisplayed)
            .ToList();

        Candidates.Clear();
        foreach (var c in filtered)
            Candidates.Add(c);

        IsActive = Candidates.Count > 0;
    }

    /// <summary>후보 선택 → 상위에 알린 뒤 닫는다.</summary>
    [RelayCommand]
    private void Select(MentionCandidate? candidate)
    {
        if (candidate is null) return;
        MemberSelected?.Invoke(this, candidate);
        Close();
    }

    /// <summary>Popup을 닫는다(전송/포커스 이탈/일치 없음).</summary>
    public void Close()
    {
        IsActive = false;
        Candidates.Clear();
        Query = string.Empty;
    }

    /// <summary>후보 선택 시 (memberId, displayName) 전달 → ChatRoomViewModel이 Draft 삽입/mentions 누적.</summary>
    public event EventHandler<MentionCandidate>? MemberSelected;

    /// <summary>caret 직전의 `@토큰`을 추출. 없으면 null(Popup 닫음).</summary>
    private static string? ExtractMentionToken(string draft, int caretIndex)
    {
        if (string.IsNullOrEmpty(draft)) return null;
        var caret = Math.Clamp(caretIndex, 0, draft.Length);

        var start = caret - 1;
        while (start >= 0 && !char.IsWhiteSpace(draft[start]) && draft[start] != '@')
            start--;

        if (start < 0 || draft[start] != '@') return null;

        // '@' 바로 앞이 문자(이메일 등)면 멘션으로 보지 않음.
        if (start > 0 && !char.IsWhiteSpace(draft[start - 1])) return null;

        return draft[(start + 1)..caret];
    }
}

/// <summary>멘션 후보(memberId + 표시명).</summary>
public sealed record MentionCandidate(string MemberId, string DisplayName);
