using System;
using System.Collections.Generic;
using System.Linq;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 이름 기반 조회가 빗나갔을 때 "혹시 이거였나요?" 후보를 뽑는 매처.
///
/// 왜 필요한가: 종전 도구들은 이름이 안 맞으면 "경로가 존재하지 않습니다"로 끝났다. 모델 입장에선
/// 다음 수를 정할 정보가 0이라, 그대로 "없습니다"라고 보고하고 끝나거나 엉뚱한 경로를 찍어보는 수밖에 없었다.
/// 실패에 후보를 실어 보내면 모델이 스스로 재시도하거나 사용자에게 되물을 수 있다 —
/// 프롬프트가 아니라 도구가 회복 가능성을 제공해야 한다.
///
/// 점수는 낮을수록 좋다(거리 개념). 순서대로 시도한다:
///   0  정확히 일치(대소문자 무시)
///   1  한쪽이 다른 쪽을 포함(부분 문자열)
///   2  부분 순서 일치(subsequence) — "mwvm" → "MainWindowViewModel"
///   3+ 편집 거리(Levenshtein)
/// </summary>
internal static class FuzzyMatch
{
    /// <summary>이 값을 넘는 점수는 "닮지 않음"으로 보고 후보에서 뺀다.</summary>
    private const int MaxAcceptableScore = 6;

    /// <summary>편집 거리 계산 상한 — 지나치게 긴 문자열끼리는 비교하지 않는다(O(n·m) 방어).</summary>
    private const int MaxLengthForDistance = 64;

    /// <summary>
    /// <paramref name="candidates"/> 중 <paramref name="query"/> 와 닮은 것을 점수 오름차순으로 최대 <paramref name="take"/>개.
    /// </summary>
    public static List<string> Best(string query, IEnumerable<string> candidates, int take = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || candidates is null)
            return [];

        return candidates
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => (Candidate: c, Score: Score(query, c)))
            .Where(x => x.Score <= MaxAcceptableScore)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Candidate.Length)     // 동점이면 짧은 쪽이 대개 더 정확한 후보다
            .ThenBy(x => x.Candidate, StringComparer.OrdinalIgnoreCase)   // 안정적 순서
            .Select(x => x.Candidate)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }

    /// <summary>낮을수록 닮음. <see cref="int.MaxValue"/> 는 비교 불가.</summary>
    public static int Score(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate))
            return int.MaxValue;

        if (string.Equals(query, candidate, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            query.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (IsSubsequence(query, candidate))
            return 2;

        // 길이 차가 크면 편집 거리를 볼 것도 없다(계산 낭비 + 어차피 안 닮음).
        if (Math.Abs(query.Length - candidate.Length) > MaxAcceptableScore)
            return int.MaxValue;

        if (query.Length > MaxLengthForDistance || candidate.Length > MaxLengthForDistance)
            return int.MaxValue;

        var distance = Levenshtein(query, candidate);
        // 부분 문자열/subsequence 보다 항상 나쁜 점수가 되도록 3부터 시작시킨다.
        return distance <= 0 ? 3 : distance + 2;
    }

    /// <summary>query 의 문자들이 순서를 지키며 candidate 안에 흩어져 있는지("mwvm" ⊂ "MainWindowViewModel").</summary>
    private static bool IsSubsequence(string query, string candidate)
    {
        var qi = 0;
        foreach (var c in candidate)
        {
            if (qi >= query.Length) break;
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
                qi++;
        }
        return qi == query.Length;
    }

    /// <summary>편집 거리(두 행 롤링 — 전체 행렬을 들지 않는다).</summary>
    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
