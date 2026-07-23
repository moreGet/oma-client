using System;
using System.Collections.Generic;
using System.IO;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// grep/glob 트리 순회 시 기본 제외 디렉토리(VCS·빌드 산출물·의존성). 노이즈·지연·오탐을 줄인다.
/// </summary>
internal static class PathIgnore
{
    private static readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "target"
    };

    /// <summary>경로의 마지막 세그먼트(폴더명)가 기본 제외 대상이면 true.</summary>
    public static bool IsIgnoredDir(string path) => Dirs.Contains(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
}
