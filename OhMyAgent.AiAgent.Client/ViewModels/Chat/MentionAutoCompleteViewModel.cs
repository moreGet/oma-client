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
///
/// 토큰 구간(<see cref="TokenStart"/>~<see cref="TokenEnd"/>)을 함께 노출한다. 상위는 이 구간을
/// 선택한 이름으로 <b>치환</b>해야 한다 — 끝에 덧붙이면 입력하던 "@홍" 이 남아 "@홍 @홍길동 " 이 된다.
/// </summary>
public sealed partial class MentionAutoCompleteViewModel : ObservableObject
{
    /// <summary>Popup 에 노출할 최대 후보 수(과도한 목록 방지).</summary>
    private const int MaxCandidatesDisplayed = 8;

    /// <summary>방의 전체 멤버 후보(memberId). 표시명 미제공이라 memberId를 라벨로도 사용.</summary>
    private readonly List<MentionCandidate> _all = [];

    /// <summary>현재 입력 토큰으로 필터된 후보(Popup 리스트 바인딩).</summary>
    public ObservableCollection<MentionCandidate> Candidates { get; } = [];

    /// <summary>
    /// Popup 표시 여부(@ 입력 + 후보 존재 시 true).
    /// Popup.IsOpen 과 <b>TwoWay</b> 로 묶인다 — StaysOpen=False 인 Popup 이 바깥 클릭으로 스스로 닫힐 때
    /// 이 값이 false 로 돌아와야 다음 입력에서 다시 열린다(OneWay 면 true 로 굳어 영영 안 열림).
    /// </summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>현재 필터 쿼리(@ 뒤 부분 토큰). 디버그/표시용.</summary>
    [ObservableProperty] private string _query = string.Empty;

    /// <summary>키보드로 이동 중인 후보 인덱스(-1=없음). Enter/Tab 이 이 항목을 확정한다.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Draft 내 `@` 위치(치환 시작). 비활성 시 -1.</summary>
    public int TokenStart { get; private set; } = -1;

    /// <summary>Draft 내 토큰 끝(=caret). 치환 종료 지점. 비활성 시 -1.</summary>
    public int TokenEnd { get; private set; } = -1;

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
        var span = ExtractMentionToken(draft, caretIndex);
        if (span is null)
        {
            Close();
            return;
        }

        var (start, end, token) = span.Value;
        TokenStart = start;
        TokenEnd = end;
        Query = token;

        var filtered = _all
            .Where(c => c.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Take(MaxCandidatesDisplayed)
            .ToList();

        // 재구성 전에 이전 선택 이름을 기억해 두면, 한 글자 더 입력해도 선택이 유지된다.
        var previous = SelectedIndex >= 0 && SelectedIndex < Candidates.Count ? Candidates[SelectedIndex].MemberId : null;

        Candidates.Clear();
        foreach (var c in filtered)
        {
            c.IsSelected = false;
            Candidates.Add(c);
        }

        var restored = previous is null ? -1 : filtered.FindIndex(c => string.Equals(c.MemberId, previous, StringComparison.Ordinal));
        SetSelectedIndex(Candidates.Count == 0 ? -1 : restored >= 0 ? restored : 0);

        IsActive = Candidates.Count > 0;
    }

    /// <summary>키보드 ↑/↓ 이동(순환). 후보가 없으면 무시.</summary>
    public void MoveSelection(int delta)
    {
        if (Candidates.Count == 0) return;
        var next = SelectedIndex < 0 ? 0 : SelectedIndex + delta;
        if (next < 0) next = Candidates.Count - 1;
        else if (next >= Candidates.Count) next = 0;
        SetSelectedIndex(next);
    }

    /// <summary>Enter/Tab 확정. 실제로 확정했으면 true(호출자가 키 입력을 소비).</summary>
    public bool CommitSelection()
    {
        if (!IsActive || Candidates.Count == 0) return false;
        var index = SelectedIndex >= 0 && SelectedIndex < Candidates.Count ? SelectedIndex : 0;
        Select(Candidates[index]);
        return true;
    }

    /// <summary>후보 선택 → 상위에 알린 뒤 닫는다.</summary>
    [RelayCommand]
    private void Select(MentionCandidate? candidate)
    {
        if (candidate is null) return;
        MemberSelected?.Invoke(this, candidate);   // 상위가 TokenStart~TokenEnd 를 치환한다(Close 전에 발화)
        Close();
    }

    /// <summary>Popup을 닫는다(전송/포커스 이탈/일치 없음).</summary>
    public void Close()
    {
        IsActive = false;
        SetSelectedIndex(-1);
        Candidates.Clear();
        Query = string.Empty;
        TokenStart = -1;
        TokenEnd = -1;
    }

    /// <summary>후보 선택 시 (memberId, displayName) 전달 → ChatRoomViewModel이 Draft 삽입/mentions 누적.</summary>
    public event EventHandler<MentionCandidate>? MemberSelected;

    private void SetSelectedIndex(int index)
    {
        for (var i = 0; i < Candidates.Count; i++)
            Candidates[i].IsSelected = i == index;
        SelectedIndex = index;
    }

    /// <summary>caret 직전의 `@토큰` 구간을 추출. 없으면 null(Popup 닫음).</summary>
    private static (int Start, int End, string Token)? ExtractMentionToken(string draft, int caretIndex)
    {
        if (string.IsNullOrEmpty(draft)) return null;
        var caret = Math.Clamp(caretIndex, 0, draft.Length);

        var start = caret - 1;
        while (start >= 0 && !char.IsWhiteSpace(draft[start]) && draft[start] != '@')
            start--;

        if (start < 0 || draft[start] != '@') return null;

        // '@' 바로 앞이 문자(이메일 등)면 멘션으로 보지 않음.
        if (start > 0 && !char.IsWhiteSpace(draft[start - 1])) return null;

        return (start, caret, draft[(start + 1)..caret]);
    }
}

/// <summary>
/// 멘션 후보(memberId + 표시명). Popup 이 키보드 이동을 하이라이트로 보여줘야 하므로
/// record 가 아니라 <see cref="IsSelected"/> 를 통지하는 관찰 가능 객체다.
/// </summary>
public sealed partial class MentionCandidate(string memberId, string displayName) : ObservableObject
{
    public string MemberId { get; } = memberId;
    public string DisplayName { get; } = displayName;

    /// <summary>키보드 이동 하이라이트(Popup DataTrigger).</summary>
    [ObservableProperty] private bool _isSelected;
}
