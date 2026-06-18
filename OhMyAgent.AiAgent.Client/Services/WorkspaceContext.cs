using System;
using System.IO;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class WorkspaceContext : IWorkspaceContext
{
    private string _root;
    private string _realRoot;

    public WorkspaceContext(ISettingsService settings)
    {
        _root = Normalize(settings.Current.WorkspaceRoot);
        _realRoot = RealPath(_root);
    }

    public string Root => _root;

    public void SetRoot(string root)
    {
        _root = Normalize(root);
        _realRoot = RealPath(_root);
    }

    public string ResolvePath(string relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
            return _root;

        var combined = Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(_root, relativeOrAbsolute);

        string full;
        try
        {
            full = Path.GetFullPath(combined);
        }
        catch (Exception ex)
        {
            throw new AgentException($"잘못된 경로입니다: {relativeOrAbsolute}", ex);
        }

        // 1) 사전적(lexical) 검증: ".." / 절대경로 탈출 차단.
        if (!IsInside(full, _root))
            throw new AgentException($"경로가 작업 디렉토리를 벗어났습니다: {relativeOrAbsolute}");

        // 2) R1: 심볼릭 링크/정션(junction) 해석 후 실제 경로 재검증 —
        //    워크스페이스 내부에서 외부를 가리키는 링크를 통한 탈출 차단.
        if (!IsInside(RealPath(full), _realRoot))
            throw new AgentException($"경로가 링크를 통해 작업 디렉토리를 벗어났습니다: {relativeOrAbsolute}");

        return full;
    }

    public bool IsInsideWorkspace(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        return IsInside(full, _root) && IsInside(RealPath(full), _realRoot);
    }

    private static bool IsInside(string full, string root)
    {
        if (string.IsNullOrEmpty(full) || string.IsNullOrEmpty(root))
            return false;

        // 루트 자체이거나 루트 하위.
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // 존재하는 가장 가까운 상위 경로의 링크(심볼릭/정션) 최종 대상을 해석해 실제 경로를 만든다.
    // 아직 존재하지 않는 경로(예: write_file 신규 생성)는 존재하는 조상까지 해석 후 나머지를 이어붙인다.
    private static string RealPath(string full)
    {
        try
        {
            var current = full;
            while (!string.IsNullOrEmpty(current))
            {
                FileSystemInfo? info =
                    File.Exists(current) ? new FileInfo(current) :
                    Directory.Exists(current) ? new DirectoryInfo(current) : null;

                if (info != null)
                {
                    var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
                    var tail = full.Length > current.Length
                        ? full[current.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : string.Empty;
                    return string.IsNullOrEmpty(tail)
                        ? resolved
                        : Path.GetFullPath(Path.Combine(resolved, tail));
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                    break;
                current = parent;
            }
        }
        catch
        {
            // 링크 해석 실패 시 사전적 경로로 폴백(1차 검증은 이미 통과).
        }

        return full;
    }

    private static string Normalize(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        }
    }
}
