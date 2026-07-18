using System;
using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Services.Diff;

/// <summary>한 diff 줄의 종류.</summary>
public enum DiffKind
{
    /// <summary>양쪽에 공통(문맥).</summary>
    Context,
    /// <summary>새 내용에만 있음(추가).</summary>
    Added,
    /// <summary>옛 내용에만 있음(삭제).</summary>
    Removed,
}

/// <summary>diff 한 줄.</summary>
public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>
/// LCS(최장 공통 부분수열) 기반 라인 diff. 순수 함수 — WPF 비의존.
///
/// edit_file 승인 카드에서 old_string→new_string 을 보여주는 데 쓴다. 사용자가 생 JSON 대신
/// 실제 변경(무엇이 지워지고 무엇이 추가되는지)을 보고 승인하도록 — 안전성 향상.
///
/// 대상이 도구 인자(대개 함수 하나~수십 줄)라 O(n·m) LCS 로 충분하다. 폭주 방지를 위해
/// 줄 수·줄 길이에 상한을 둔다.
/// </summary>
public static class LineDiff
{
    /// <summary>diff 로 표시할 최대 줄 수. 초과분은 잘라내고 안내 줄을 붙인다.</summary>
    private const int MaxLines = 400;

    /// <summary>한 줄 최대 길이(초과 시 말줄임). 미니파이된 파일 등에서 카드가 터지지 않게.</summary>
    private const int MaxLineLength = 500;

    public static IReadOnlyList<DiffLine> Compute(string? oldText, string? newText)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);

        var result = new List<DiffLine>();
        // LCS 길이 테이블(뒤에서 앞으로).
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        // 백트래킹으로 diff 시퀀스 복원.
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                result.Add(new DiffLine(DiffKind.Context, Clip(a[x])));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add(new DiffLine(DiffKind.Removed, Clip(a[x])));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, Clip(b[y])));
                y++;
            }

            if (result.Count >= MaxLines)
                return Truncate(result);
        }
        while (x < a.Length) { result.Add(new DiffLine(DiffKind.Removed, Clip(a[x++]))); if (result.Count >= MaxLines) return Truncate(result); }
        while (y < b.Length) { result.Add(new DiffLine(DiffKind.Added, Clip(b[y++]))); if (result.Count >= MaxLines) return Truncate(result); }

        return result;
    }

    private static string[] SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Clip(string line)
        => line.Length > MaxLineLength ? line[..MaxLineLength] + " …" : line;

    private static List<DiffLine> Truncate(List<DiffLine> lines)
    {
        lines.Add(new DiffLine(DiffKind.Context, $"… (변경이 커서 {MaxLines}줄에서 잘림)"));
        return lines;
    }
}
