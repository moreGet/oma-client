using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class WorkspaceContext : IWorkspaceContext
{
    // 활성 루트 전체. 첫 항목이 주 루트. 항상 최소 1개(빈 입력이면 Desktop 폴백).
    private List<(string root, string realRoot)> _roots = [];

    public WorkspaceContext(ISettingsService settings)
    {
        var enabled = settings.Current.Workspaces
            ?.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Path))
            .Select(w => w.Path)
            .ToList() ?? [];

        if (enabled.Count > 0)
            SetRoots(enabled);
        else
            SetRoot(settings.Current.WorkspaceRoot);
    }

    public string Root => _roots.Count > 0 ? _roots[0].root : Normalize("");

    public IReadOnlyList<string> Roots => _roots.Select(r => r.root).ToList();

    public void SetRoot(string root) => SetRoots([root]);

    public void SetRoots(IReadOnlyList<string> roots)
    {
        var normalized = (roots ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => (root: r, realRoot: RealPath(r)))
            .ToList();

        // 빈 목록이면 Desktop 폴백 단일 루트 보장.
        if (normalized.Count == 0)
        {
            var fallback = Normalize("");
            normalized = [ (fallback, RealPath(fallback)) ];
        }

        _roots = normalized;
    }

    public string ResolvePath(string relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
            return Root;

        // 상대 경로는 주 루트 기준 결합.
        var combined = Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(Root, relativeOrAbsolute);

        string full;
        try
        {
            full = Path.GetFullPath(combined);
        }
        catch (Exception ex)
        {
            throw new AgentException($"잘못된 경로입니다: {relativeOrAbsolute}", ex);
        }

        var real = RealPath(full);

        // 활성 루트 중 하나라도 양 단계(사전적 + 링크 해석) 모두 통과하면 허용.
        foreach (var (root, realRoot) in _roots)
        {
            if (IsInside(full, root) && IsInside(real, realRoot))
                return full;
        }

        throw new AgentException($"경로가 작업 디렉토리를 벗어났습니다: {relativeOrAbsolute}");
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

        var real = RealPath(full);
        return _roots.Any(r => IsInside(full, r.root) && IsInside(real, r.realRoot));
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
