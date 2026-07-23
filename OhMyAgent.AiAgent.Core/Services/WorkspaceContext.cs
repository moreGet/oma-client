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
            .Select(r => (root: r, realRoot: RealRoot(r)))
            .ToList();

        // 빈 목록이면 Desktop 폴백 단일 루트 보장.
        if (normalized.Count == 0)
        {
            var fallback = Normalize("");
            normalized = [ (fallback, RealRoot(fallback)) ];
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
        if (real is null)
            throw new AgentException($"경로를 확인할 수 없어 거부했습니다: {relativeOrAbsolute}");

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
        if (real is null)
            return false;

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

    // 경로를 드라이브 루트부터 세그먼트 단위로 내려가며 "각 단계마다" 링크(심볼릭/정션)를 해석한다.
    //
    // 리프만 해석하면 안 된다: <root>\link\secret.txt 처럼 중간 디렉토리가 정션인 경우
    // 리프(secret.txt)는 재분석 지점이 아니므로 ResolveLinkTarget 이 null 을 반환하고,
    // 사전적 경로가 그대로 통과해 샌드박스가 무력화된다. 에이전트는 run_command 로
    // mklink /j 를 직접 실행할 수 있으므로(관리자 권한 불필요) 스스로 탈출구를 만들 수 있다.
    //
    // 아직 존재하지 않는 구간(예: write_file 신규 생성)은 링크일 수 없으므로 그대로 이어붙인다.
    // 해석 실패 시 null 을 반환하며, 호출자는 반드시 거부해야 한다(사전적 경로 폴백 금지 —
    // 폴백하면 접근 거부 등으로 해석이 막힌 링크가 검증을 우회하게 된다).
    private static string? RealPath(string full)
    {
        try
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return null;

            var segments = full[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            var current = root;
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);

                FileSystemInfo? info =
                    File.Exists(current) ? new FileInfo(current) :
                    Directory.Exists(current) ? new DirectoryInfo(current) : null;

                // 존재하지 않는 구간부터는 링크가 있을 수 없다.
                if (info is null) continue;

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    current = Path.GetFullPath(target.FullName);
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }
        catch
        {
            // 해석 불가(접근 거부/순환 링크/잘못된 경로) → 판단 불가이므로 거부한다.
            return null;
        }
    }

    // 루트 자체의 해석 실패는 사전적 경로로 폴백한다. 루트는 사용자가 직접 설정한 신뢰 입력이고,
    // 폴백은 검사를 "더 엄격하게"만 만들 뿐(해석된 하위 경로가 사전적 루트에 속할 수 없으므로)
    // 느슨하게 만들지 않는다. 반면 검사 대상 경로의 해석 실패는 위와 같이 거부해야 한다.
    private static string RealRoot(string root) => RealPath(root) ?? root;

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
