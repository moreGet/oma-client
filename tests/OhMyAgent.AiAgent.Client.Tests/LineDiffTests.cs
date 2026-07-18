using System.Linq;
using OhMyAgent.AiAgent.Client.Services.Diff;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// edit_file 승인 카드의 diff. 사용자가 이걸 보고 파일 변경을 승인하므로, "무엇이 지워지고 무엇이
/// 추가되는지"를 정확히 표현해야 한다 — 틀리면 사용자가 잘못된 변경을 승인한다.
/// </summary>
public class LineDiffTests
{
    private static string[] Lines(System.Collections.Generic.IEnumerable<DiffLine> d, DiffKind kind)
        => d.Where(l => l.Kind == kind).Select(l => l.Text).ToArray();

    [Fact]
    public void SingleLineChange_ShowsRemovedThenAdded()
    {
        var d = LineDiff.Compute("var x = 1;", "var x = 2;");

        Assert.Contains("var x = 1;", Lines(d, DiffKind.Removed));
        Assert.Contains("var x = 2;", Lines(d, DiffKind.Added));
    }

    [Fact]
    public void UnchangedLines_AreContext()
    {
        var d = LineDiff.Compute("a\nb\nc", "a\nX\nc");

        Assert.Contains("a", Lines(d, DiffKind.Context));
        Assert.Contains("c", Lines(d, DiffKind.Context));
        Assert.Contains("b", Lines(d, DiffKind.Removed));
        Assert.Contains("X", Lines(d, DiffKind.Added));
    }

    [Fact]
    public void PureAddition_AllAdded()
    {
        var d = LineDiff.Compute("", "새 줄1\n새 줄2");

        Assert.Empty(Lines(d, DiffKind.Removed));
        Assert.Equal(2, Lines(d, DiffKind.Added).Length);
    }

    [Fact]
    public void PureDeletion_AllRemoved()
    {
        var d = LineDiff.Compute("지울 줄1\n지울 줄2", "");

        Assert.Empty(Lines(d, DiffKind.Added));
        Assert.Equal(2, Lines(d, DiffKind.Removed).Length);
    }

    [Fact]
    public void Identical_AllContext_NoChanges()
    {
        var d = LineDiff.Compute("a\nb", "a\nb");

        Assert.Empty(Lines(d, DiffKind.Added));
        Assert.Empty(Lines(d, DiffKind.Removed));
        Assert.Equal(2, Lines(d, DiffKind.Context).Length);
    }

    [Fact]
    public void InsertionInMiddle_KeepsSurroundingContext()
    {
        var d = LineDiff.Compute("first\nlast", "first\nmiddle\nlast");

        // first/last 는 문맥으로 보존, middle 만 추가.
        Assert.Contains("first", Lines(d, DiffKind.Context));
        Assert.Contains("last", Lines(d, DiffKind.Context));
        Assert.Equal(new[] { "middle" }, Lines(d, DiffKind.Added));
        Assert.Empty(Lines(d, DiffKind.Removed));
    }

    [Fact]
    public void PreservesLineOrder()
    {
        // 결과를 순서대로 재구성하면 old(제거+문맥)와 new(추가+문맥)가 원문과 일치해야 한다.
        var d = LineDiff.Compute("a\nb\nc\nd", "a\nc\nd\ne");

        var reconstructedOld = string.Join("\n",
            d.Where(l => l.Kind != DiffKind.Added).Select(l => l.Text));
        var reconstructedNew = string.Join("\n",
            d.Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text));

        Assert.Equal("a\nb\nc\nd", reconstructedOld);
        Assert.Equal("a\nc\nd\ne", reconstructedNew);
    }

    [Fact]
    public void LongLine_IsClipped()
    {
        var huge = new string('x', 2000);
        var d = LineDiff.Compute(huge, "");

        Assert.All(d, l => Assert.True(l.Text.Length < 600));
    }

    [Fact]
    public void ManyChanges_AreTruncated()
    {
        var old = string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"old{i}"));
        var neu = string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"new{i}"));

        var d = LineDiff.Compute(old, neu);

        // 상한 + 안내 줄 하나. 카드가 터지지 않게.
        Assert.True(d.Count <= 401);
        Assert.Contains(d, l => l.Text.Contains("잘림"));
    }

    [Fact]
    public void HandlesCrlf()
    {
        var d = LineDiff.Compute("a\r\nb", "a\r\nc");

        // \r 이 텍스트에 섞여 오탐을 만들면 안 된다.
        Assert.Contains("a", Lines(d, DiffKind.Context));
        Assert.Contains("b", Lines(d, DiffKind.Removed));
        Assert.Contains("c", Lines(d, DiffKind.Added));
    }
}
