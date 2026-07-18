using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 입력창의 <c>@파일</c> 자동완성. <c>@</c> 뒤 토큰으로 워크스페이스 파일을 필터해 팝업 후보로 노출하고,
/// 선택 시 상대 경로를 삽입한다(Claude Code 의 @file 에 대응).
///
/// 검증된 부품 재사용: 인덱스 열거는 <see cref="SafeFileWalk"/>(정션 미traversal + 무시 디렉토리 스킵),
/// 필터는 <see cref="FuzzyMatch"/>(오타·부분·subsequence). 토큰 추출은 순수 <see cref="FileMentions"/>.
/// </summary>
public sealed partial class FileMentionViewModel : ObservableObject
{
    /// <summary>팝업에 노출할 최대 후보 수.</summary>
    private const int MaxCandidates = 8;

    /// <summary>인덱싱 파일 상한(거대 트리에서 멘션이 느려지지 않게).</summary>
    private const int MaxIndexed = 5000;

    private readonly IWorkspaceContext _workspace;
    private List<string> _index = [];   // 워크스페이스 상대 경로('/' 정규화)

    /// <summary>현재 토큰으로 필터된 후보(팝업 리스트 바인딩).</summary>
    public ObservableCollection<string> Candidates { get; } = [];

    /// <summary>팝업 표시 여부(@토큰 + 후보 존재 시 true).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>키보드 내비게이션용 선택 인덱스(팝업 ListBox 바인딩).</summary>
    [ObservableProperty] private int _selectedIndex = -1;

    // 선택 시 대체할 토큰 범위: 입력 텍스트의 [Start, End) 를 "@경로 "로 바꾼다.
    private int _tokenStart = -1;
    private int _tokenEnd = -1;

    public FileMentionViewModel(IWorkspaceContext workspace) => _workspace = workspace;

    /// <summary>
    /// 입력과 caret 으로 팝업을 갱신한다. @토큰이 없으면 닫는다. 멘션이 "새로" 시작될 때(빈 토큰) 인덱스를 다시 만든다
    /// — 파일이 그 사이 바뀌었을 수 있으므로. 이후 타이핑은 캐시된 인덱스를 필터만 한다.
    /// </summary>
    public void UpdateFromInput(string? text, int caret)
    {
        var token = FileMentions.ExtractToken(text, caret);
        if (token is null)
        {
            Close();
            return;
        }

        _tokenStart = token.Start;
        _tokenEnd = caret;

        // 멘션 시작(@ 직후) 이거나 인덱스가 비어 있으면 새로 만든다.
        if (token.Text.Length == 0 || _index.Count == 0)
            RebuildIndex();

        var matches = token.Text.Length == 0
            ? _index.Take(MaxCandidates).ToList()                          // 갓 '@' — 앞쪽 파일 몇 개를 보여준다.
            : FuzzyMatch.Best(token.Text, _index, MaxCandidates);          // 부분/오타 매칭.

        Candidates.Clear();
        foreach (var m in matches)
            Candidates.Add(m);

        IsActive = Candidates.Count > 0;
        SelectedIndex = IsActive ? 0 : -1;
    }

    /// <summary>선택된(또는 지정) 후보로 토큰을 대체한 새 입력 텍스트와 caret 위치를 만든다. 실패 시 null.</summary>
    public (string Text, int Caret)? Accept(string fullText, string? path = null)
    {
        var chosen = path ?? (SelectedIndex >= 0 && SelectedIndex < Candidates.Count ? Candidates[SelectedIndex] : null);
        if (chosen is null || _tokenStart < 0)
            return null;

        var end = Math.Clamp(_tokenEnd, 0, fullText.Length);
        var start = Math.Clamp(_tokenStart, 0, end);

        var replacement = $"@{chosen} ";                                  // 뒤에 공백 — 이어서 타이핑 편하게.
        var newText = fullText[..start] + replacement + fullText[end..];
        var caret = start + replacement.Length;

        Close();
        return (newText, caret);
    }

    public void MoveSelection(int delta)
    {
        if (Candidates.Count == 0) return;
        SelectedIndex = ((SelectedIndex + delta) % Candidates.Count + Candidates.Count) % Candidates.Count;
    }

    public void Close()
    {
        IsActive = false;
        Candidates.Clear();
        SelectedIndex = -1;
        _tokenStart = _tokenEnd = -1;
    }

    private void RebuildIndex()
    {
        var paths = new List<string>(MaxIndexed);
        var skipped = new List<string>();

        try
        {
            foreach (var root in _workspace.Roots)
            {
                foreach (var file in SafeFileWalk.EnumerateFiles(root, skipped, CancellationToken.None, skipIgnoredDirs: true))
                {
                    if (paths.Count >= MaxIndexed) break;
                    paths.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
                if (paths.Count >= MaxIndexed) break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("FileMention", "파일 인덱스 생성 실패", ex);
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        _index = paths;
    }
}
