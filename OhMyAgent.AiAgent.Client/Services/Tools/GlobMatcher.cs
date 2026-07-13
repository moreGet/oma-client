using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 외부 라이브러리 없이 `**`, `*`, `?` 를 지원하는 경량 glob 매처.
/// 매칭은 baseDir 기준 상대 경로(슬래시 정규화)에 대해 수행.
/// </summary>
internal static class GlobMatcher
{
    public static IEnumerable<string> EnumerateFiles(string baseDir, string pattern, int cap = 1000)
    {
        var regex = Compile(pattern);
        var results = new List<string>();

        foreach (var file in SafeEnumerate(baseDir))
        {
            var rel = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            if (regex.IsMatch(rel))
            {
                results.Add(rel);
                if (results.Count >= cap)
                    break;
            }
        }

        return results;
    }

    private static IEnumerable<string> SafeEnumerate(string baseDir)
    {
        var pending = new Stack<string>();
        pending.Push(baseDir);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var f in files)
                yield return f;
            foreach (var d in subDirs)
                if (!PathIgnore.IsIgnoredDir(d)) pending.Push(d);   // .git/bin/obj/node_modules 등 제외
        }
    }

    public static Regex Compile(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < normalized.Length)
        {
            var c = normalized[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        // ** — 모든 경로 (디렉토리 구분자 포함). 뒤따르는 '/' 흡수.
                        sb.Append(".*");
                        i += 2;
                        if (i < normalized.Length && normalized[i] == '/')
                            i++;
                        continue;
                    }
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
            i++;
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
