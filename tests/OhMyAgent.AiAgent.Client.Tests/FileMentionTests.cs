using System;
using System.IO;
using System.Linq;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.ViewModels;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// @파일 멘션. 토큰 추출(순수)과 인덱스/필터/삽입(워크스페이스)을 검증한다.
/// 오분류하면 일반 이메일이 멘션으로 잡히거나, 삽입이 엉뚱한 텍스트를 덮어쓴다.
/// </summary>
public class FileMentionTests
{
    // ── 토큰 추출(순수) ──

    [Fact]
    public void ExtractToken_AtStart()
    {
        var t = FileMentions.ExtractToken("@src/Main", 9);
        Assert.NotNull(t);
        Assert.Equal(0, t!.Start);
        Assert.Equal("src/Main", t.Text);
    }

    [Fact]
    public void ExtractToken_AfterSpace()
    {
        var t = FileMentions.ExtractToken("이거 봐 @foo.cs", 12);
        Assert.NotNull(t);
        Assert.Equal("foo.cs", t!.Text);
    }

    [Fact]
    public void ExtractToken_IncludesPathChars()
    {
        // 경로 문자(/ . -)는 토큰의 일부 — 공백에서만 끊는다.
        var t = FileMentions.ExtractToken("@a/b-c.d/e", 10);
        Assert.Equal("a/b-c.d/e", t!.Text);
    }

    [Fact]
    public void ExtractToken_JustAt_EmptyToken()
    {
        var t = FileMentions.ExtractToken("hi @", 4);
        Assert.NotNull(t);
        Assert.Equal("", t!.Text);   // 갓 '@' — 팝업이 열려 앞쪽 파일을 보여준다.
    }

    [Theory]
    [InlineData("user@host.com", 13)]   // 이메일 — '@' 앞이 문자
    [InlineData("no mention here", 5)]
    [InlineData("", 0)]
    public void ExtractToken_NotAMention_ReturnsNull(string text, int caret)
        => Assert.Null(FileMentions.ExtractToken(text, caret));

    [Fact]
    public void ExtractToken_StopsAtSpaceBeforeCaret()
    {
        // caret 앞 토큰만 본다 — 앞선 @는 무시.
        var t = FileMentions.ExtractToken("@first 그리고 @sec", 15);
        Assert.Equal("sec", t!.Text);
    }

    [Fact]
    public void ExtractToken_ClampsCaret()
    {
        Assert.NotNull(FileMentions.ExtractToken("@x", 999));   // 범위 넘는 caret 도 안전.
    }

    // ── 인덱스/필터/삽입(워크스페이스) ──

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; }
        public WorkspaceContext Ws { get; }

        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "omg-mention", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            Directory.CreateDirectory(Path.Combine(Root, "node_modules", "junk"));
            File.WriteAllText(Path.Combine(Root, "src", "MainWindow.xaml.cs"), "x");
            File.WriteAllText(Path.Combine(Root, "src", "SecurityValidator.cs"), "x");
            File.WriteAllText(Path.Combine(Root, "README.md"), "x");
            File.WriteAllText(Path.Combine(Root, "node_modules", "junk", "noise.cs"), "x");

            Ws = new WorkspaceContext(new FakeSettingsService());
            Ws.SetRoot(Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* 임시 폴더 */ }
        }
    }

    [Fact]
    public void Filter_FindsFileByPartialName()
    {
        using var w = new TempWorkspace();
        var vm = new FileMentionViewModel(w.Ws);

        vm.UpdateFromInput("@Main", 5);

        Assert.True(vm.IsActive);
        Assert.Contains(vm.Candidates, c => c.EndsWith("MainWindow.xaml.cs"));
    }

    [Fact]
    public void Filter_ExcludesIgnoredDirs()
    {
        using var w = new TempWorkspace();
        var vm = new FileMentionViewModel(w.Ws);

        vm.UpdateFromInput("@noise", 6);

        // node_modules 안의 파일은 후보에 없어야 한다.
        Assert.DoesNotContain(vm.Candidates, c => c.Contains("node_modules"));
    }

    [Fact]
    public void Filter_EmptyToken_ShowsSomeFiles()
    {
        using var w = new TempWorkspace();
        var vm = new FileMentionViewModel(w.Ws);

        vm.UpdateFromInput("@", 1);

        Assert.True(vm.IsActive);
        Assert.NotEmpty(vm.Candidates);
    }

    [Fact]
    public void Accept_ReplacesTokenWithPath()
    {
        using var w = new TempWorkspace();
        var vm = new FileMentionViewModel(w.Ws);
        vm.UpdateFromInput("@Main", 5);

        var chosen = vm.Candidates.First(c => c.EndsWith("MainWindow.xaml.cs"));
        var applied = vm.Accept("@Main", chosen);

        Assert.NotNull(applied);
        Assert.Equal($"@{chosen} ", applied!.Value.Text);          // 토큰이 전체 경로로 대체 + 뒤 공백.
        Assert.Equal(applied.Value.Text.Length, applied.Value.Caret);
    }

    [Fact]
    public void Accept_ReplacesOnlyTheToken_KeepsSurroundingText()
    {
        using var w = new TempWorkspace();
        var vm = new FileMentionViewModel(w.Ws);
        vm.UpdateFromInput("이거 @Sec 고쳐줘", 7);   // caret 은 "@Sec" 의 'c' 바로 뒤(인덱스 7)

        var chosen = vm.Candidates.First(c => c.EndsWith("SecurityValidator.cs"));
        var applied = vm.Accept("이거 @Sec 고쳐줘", chosen);

        Assert.NotNull(applied);
        Assert.StartsWith("이거 @", applied!.Value.Text);
        Assert.Contains("고쳐줘", applied.Value.Text);              // 뒤 텍스트 보존.
        Assert.Contains(chosen, applied.Value.Text);
    }

    [Fact]
    public void Close_ResetsState()
    {
        using var w = new TempWorkspace();
        var vm = new FileMentionViewModel(w.Ws);
        vm.UpdateFromInput("@Main", 5);
        Assert.True(vm.IsActive);

        vm.Close();

        Assert.False(vm.IsActive);
        Assert.Empty(vm.Candidates);
        Assert.Null(vm.Accept("@Main"));   // 토큰 범위가 초기화돼 삽입 불가.
    }

    [Fact]
    public void MoveSelection_WrapsAround()
    {
        using var w = new TempWorkspace();
        var vm = new FileMentionViewModel(w.Ws);
        vm.UpdateFromInput("@", 1);
        var count = vm.Candidates.Count;

        vm.SelectedIndex = 0;
        vm.MoveSelection(-1);
        Assert.Equal(count - 1, vm.SelectedIndex);   // 위로 감기.
        vm.MoveSelection(+1);
        Assert.Equal(0, vm.SelectedIndex);           // 아래로 감기.
    }

    [Fact]
    public void NoMention_ClosesPopup()
    {
        using var w = new TempWorkspace();
        var vm = new FileMentionViewModel(w.Ws);
        vm.UpdateFromInput("@Main", 5);
        Assert.True(vm.IsActive);

        vm.UpdateFromInput("일반 텍스트", 6);   // @ 없음
        Assert.False(vm.IsActive);
    }
}
