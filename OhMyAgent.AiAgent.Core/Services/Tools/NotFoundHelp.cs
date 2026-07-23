using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// "찾지 못함"을 회복 가능한 실패로 바꾼다 — 후보와 다음 수를 함께 돌려준다.
///
/// 종전 도구들은 이름이 안 맞으면 "파일이 존재하지 않습니다: foo.cs" 로 끝났다. 모델에게 남는 정보가
/// 0이라 그대로 "없습니다"라고 보고하거나 경로를 찍어보며 헤맬 수밖에 없었다. 사용자는
/// "이름이 거의 정확해야만 찾는다"고 느끼게 된다.
///
/// 도구가 후보를 제시하면 모델이 스스로 재시도하거나("혹시 MainWindow.xaml.cs?")
/// 사용자에게 되물을 수 있다("이름으로는 못 찾았는데 내용으로 찾아볼까요?").
/// 되묻기를 프롬프트로만 시키면 근거가 없어 공허하다 — 근거는 도구가 만들어 줘야 한다.
/// </summary>
internal static class NotFoundHelp
{
    /// <summary>후보 탐색 시 훑을 파일 수 상한(거대 트리에서 실패 경로가 느려지면 안 된다).</summary>
    private const int MaxScanned = 20_000;

    /// <summary>
    /// 워크스페이스에서 <paramref name="missingPath"/> 와 닮은 파일을 찾아 안내 문구를 만든다.
    /// 후보가 없으면 내용 검색 제안만 담은 문구를 돌려준다.
    /// </summary>
    public static string ForFile(IWorkspaceContext workspace, string missingPath, CancellationToken ct = default)
    {
        var wanted = Path.GetFileName(missingPath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(wanted))
            wanted = missingPath;

        var candidates = ScanNames(workspace, ct);
        var hits = FuzzyMatch.Best(wanted, candidates.Keys, take: 5);

        if (hits.Count == 0)
        {
            return $"파일이 존재하지 않습니다: {missingPath}\n" +
                   $"워크스페이스에서 '{wanted}' 와 닮은 이름도 찾지 못했습니다.\n" +
                   "다음을 시도해 보세요: (1) glob 으로 넓게 탐색(예: \"**/*이름의일부*\"), " +
                   "(2) grep 으로 파일 '내용' 검색, (3) 사용자에게 정확한 위치를 되묻기.";
        }

        var lines = hits.Select(h => $"  - {candidates[h]}");
        return $"파일이 존재하지 않습니다: {missingPath}\n" +
               $"이름이 닮은 파일이 워크스페이스에 있습니다 — 이 중 하나를 의도했는지 확인하세요:\n" +
               string.Join("\n", lines) + "\n" +
               "의도한 것이 없다면 grep 으로 내용을 검색하거나 사용자에게 되물으세요.";
    }

    /// <summary>glob 이 0건일 때의 안내. 패턴을 어떻게 완화할지 알려준다.</summary>
    public static string ForGlob(IWorkspaceContext workspace, string pattern, CancellationToken ct = default)
    {
        // 패턴에서 와일드카드를 걷어낸 '핵심 토큰'으로 이름 후보를 찾는다(예: "src/**/Main*.cs" → "Main").
        var core = pattern
            .Replace("**", " ").Replace('*', ' ').Replace('?', ' ')
            .Replace('/', ' ').Replace('\\', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .OrderByDescending(t => t.Length)
            .FirstOrDefault()
            ?.TrimStart('.');

        if (string.IsNullOrWhiteSpace(core))
        {
            return $"패턴과 일치하는 파일이 없습니다: {pattern}\n" +
                   "패턴을 더 넓게 잡거나(예: \"**/*.확장자\") grep 으로 내용을 검색해 보세요.";
        }

        var candidates = ScanNames(workspace, ct);
        var hits = FuzzyMatch.Best(core, candidates.Keys, take: 5);

        if (hits.Count == 0)
        {
            return $"패턴과 일치하는 파일이 없습니다: {pattern}\n" +
                   $"'{core}' 와 닮은 이름도 없습니다. grep 으로 파일 '내용'을 검색하거나, " +
                   "사용자에게 찾으려는 대상을 되물으세요.";
        }

        var lines = hits.Select(h => $"  - {candidates[h]}");
        return $"패턴과 일치하는 파일이 없습니다: {pattern}\n" +
               $"'{core}' 와 이름이 닮은 파일은 있습니다:\n" +
               string.Join("\n", lines) + "\n" +
               "패턴을 이에 맞게 고치거나, 내용 검색(grep)으로 전환하세요.";
    }

    /// <summary>파일명 → 워크스페이스 상대경로. 동명이인은 먼저 만난 것을 남긴다(후보 제시용이라 충분).</summary>
    private static Dictionary<string, string> ScanNames(IWorkspaceContext workspace, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        var skipped = new List<string>();   // 링크/접근불가 — 후보 탐색에선 보고할 대상이 아니다.

        foreach (var root in workspace.Roots)
        {
            // skipIgnoredDirs: node_modules/bin/obj 안의 동명 파일이 후보를 오염시키는 것을 막고,
            // 거대 트리에서 실패 경로가 느려지지 않게 한다.
            foreach (var file in SafeFileWalk.EnumerateFiles(root, skipped, ct, skipIgnoredDirs: true))
            {
                if (++scanned > MaxScanned) return map;

                var name = Path.GetFileName(file);
                if (!map.ContainsKey(name))
                    map[name] = Path.GetRelativePath(root, file).Replace('\\', '/');
            }
        }

        return map;
    }
}
