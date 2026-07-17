using System.Collections.Generic;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// "이름이 거의 정확해야만 찾는다"를 고치는 매처. 점수는 낮을수록 닮음이며,
/// 등급(정확 &lt; 부분문자열 &lt; subsequence &lt; 편집거리)의 순서가 뒤집히지 않는 것이 핵심 계약이다.
/// </summary>
public class FuzzyMatchTests
{
    private static readonly List<string> Files =
    [
        "MainWindow.xaml.cs",
        "MainWindow.xaml",
        "AgentSessionViewModel.cs",
        "ChatRoomViewModel.cs",
        "SecurityValidator.cs",
        "WorkspaceContext.cs",
    ];

    [Fact]
    public void Score_ExactMatchIsBest()
    {
        Assert.Equal(0, FuzzyMatch.Score("MainWindow.xaml", "MainWindow.xaml"));
        Assert.Equal(0, FuzzyMatch.Score("mainwindow.xaml", "MainWindow.xaml"));   // 대소문자 무시
    }

    [Fact]
    public void Score_RanksExactBeforeSubstringBeforeSubsequence()
    {
        var exact = FuzzyMatch.Score("MainWindow", "MainWindow");
        var substring = FuzzyMatch.Score("MainWindow", "MainWindow.xaml.cs");
        var subsequence = FuzzyMatch.Score("mwvm", "MainWindowViewModel");
        var distant = FuzzyMatch.Score("MainWindwo", "MainWindow");   // 오타(전치)

        Assert.True(exact < substring, "정확 일치가 부분 문자열보다 나빠졌다");
        Assert.True(substring < subsequence, "부분 문자열이 subsequence 보다 나빠졌다");
        Assert.True(subsequence < distant, "subsequence 가 편집거리보다 나빠졌다");
    }

    [Fact]
    public void Best_FindsPartialName()
    {
        // 사용자가 "MainWindow" 만 말해도 두 파일이 잡혀야 한다.
        var hits = FuzzyMatch.Best("MainWindow", Files);

        Assert.Contains("MainWindow.xaml", hits);
        Assert.Contains("MainWindow.xaml.cs", hits);
    }

    [Fact]
    public void Best_ToleratesTypos()
    {
        // 오타 한 글자 — 종전이면 완전히 놓쳤다.
        var hits = FuzzyMatch.Best("SecurityValidatr.cs", Files);

        Assert.Contains("SecurityValidator.cs", hits);
    }

    [Fact]
    public void Best_MatchesInitialsAsSubsequence()
    {
        var hits = FuzzyMatch.Best("asvm", Files);

        Assert.Contains("AgentSessionViewModel.cs", hits);
    }

    [Fact]
    public void Best_ShorterCandidateWinsOnTie()
    {
        // 둘 다 부분 문자열(점수 동일) → 짧은 쪽이 대개 의도한 파일이다.
        var hits = FuzzyMatch.Best("MainWindow", Files);

        Assert.Equal("MainWindow.xaml", hits[0]);
    }

    [Fact]
    public void Best_ReturnsEmptyForUnrelatedQuery()
    {
        // 아무 관련 없는 질의에 억지 후보를 내밀면 모델을 오도한다.
        Assert.Empty(FuzzyMatch.Best("zzzzzzzzzzzzq", Files));
    }

    [Fact]
    public void Best_RespectsTakeLimit()
    {
        Assert.True(FuzzyMatch.Best("ViewModel", Files, take: 1).Count <= 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Best_HandlesEmptyQuery(string? query)
    {
        Assert.Empty(FuzzyMatch.Best(query!, Files));
    }

    [Fact]
    public void Best_HandlesNullCandidates()
    {
        Assert.Empty(FuzzyMatch.Best("x", null!));
    }

    [Fact]
    public void Score_IsSymmetricForContainment()
    {
        // 질의가 후보를 포함하는 경우도 부분 문자열로 인정해야 한다(사용자가 더 긴 이름을 말한 경우).
        Assert.Equal(1, FuzzyMatch.Score("MainWindow.xaml.cs.bak", "MainWindow.xaml.cs"));
    }
}
