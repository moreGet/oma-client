using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// Compile 은 순수 함수(string → Regex)라 디스크 없이 검증 가능하다.
/// 매칭 대상은 baseDir 기준 상대경로(슬래시 정규화)다.
/// </summary>
public class GlobMatcherTests
{
    private static bool Matches(string glob, string relativePath)
        => GlobMatcher.Compile(glob).IsMatch(relativePath.Replace('\\', '/'));

    [Theory]
    [InlineData("*.cs", "Program.cs")]
    [InlineData("*.cs", "A.cs")]
    public void Star_MatchesWithinSingleSegment(string glob, string path)
        => Assert.True(Matches(glob, path));

    [Theory]
    [InlineData("*.cs", "src/Program.cs")]      // '*' 는 '/' 를 넘지 않는다
    [InlineData("*.cs", "Program.csx")]
    [InlineData("*.cs", "Program.vb")]
    public void Star_DoesNotCrossDirectoryBoundary(string glob, string path)
        => Assert.False(Matches(glob, path));

    [Theory]
    [InlineData("**/*.cs", "Program.cs")]        // ** 는 0개 디렉토리에도 매치되어야 한다
    [InlineData("**/*.cs", "src/Program.cs")]
    [InlineData("**/*.cs", "src/a/b/c/Program.cs")]
    public void DoubleStar_CrossesAnyNumberOfDirectories(string glob, string path)
        => Assert.True(Matches(glob, path));

    [Theory]
    [InlineData("src/**/*.cs", "src/Program.cs")]
    [InlineData("src/**/*.cs", "src/a/b/Program.cs")]
    public void DoubleStar_WorksAfterPrefix(string glob, string path)
        => Assert.True(Matches(glob, path));

    [Fact]
    public void DoubleStar_PrefixIsStillAnchored()
        => Assert.False(Matches("src/**/*.cs", "other/Program.cs"));

    [Theory]
    [InlineData("?.cs", "A.cs", true)]
    [InlineData("?.cs", "AB.cs", false)]
    [InlineData("a?c.txt", "abc.txt", true)]
    [InlineData("?.cs", "/.cs", false)]          // '?' 도 '/' 를 넘지 않는다
    public void Question_MatchesExactlyOneNonSeparatorChar(string glob, string path, bool expected)
        => Assert.Equal(expected, Matches(glob, path));

    [Fact]
    public void Pattern_IsAnchoredAtBothEnds()
    {
        Assert.False(Matches("Program.cs", "src/Program.cs"));
        Assert.False(Matches("Program", "Program.cs"));
    }

    [Fact]
    public void Pattern_IsCaseInsensitive()
    {
        Assert.True(Matches("*.CS", "program.cs"));
        Assert.True(Matches("SRC/*.cs", "src/a.cs"));
    }

    [Fact]
    public void Backslashes_AreNormalizedToForwardSlashes()
    {
        // 호출자가 Windows 경로 구분자로 글롭을 줘도 동작해야 한다.
        Assert.True(Matches(@"src\*.cs", "src/a.cs"));
    }

    [Fact]
    public void RegexMetacharacters_AreTreatedLiterally()
    {
        // '.' '(' '+' 등이 정규식으로 해석되면 오탐이 난다.
        Assert.True(Matches("a.b.txt", "a.b.txt"));
        Assert.False(Matches("a.b.txt", "axbxtxt"));
        Assert.True(Matches("file(1).txt", "file(1).txt"));
        Assert.True(Matches("a+b.txt", "a+b.txt"));
        Assert.False(Matches("a+b.txt", "aab.txt"));
    }

    [Fact]
    public void EmptyPattern_MatchesOnlyEmptyString()
    {
        Assert.True(Matches("", ""));
        Assert.False(Matches("", "a.cs"));
    }
}
