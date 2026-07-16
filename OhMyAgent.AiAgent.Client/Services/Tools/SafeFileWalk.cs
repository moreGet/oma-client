using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 재분석 지점(정션/심볼릭 링크)을 따라가지 않는 재귀 파일 열거.
///
/// 왜 필요한가: <see cref="IWorkspaceContext.ResolvePath"/> 는 "주어진 경로 하나"만 검증한다.
/// 하위 트리를 재귀 열거하는 도구들(compress_files, copy)은 그 결과를 재검증하지 않고 그대로 사용했는데,
/// Directory.EnumerateFiles(..., AllDirectories) 는 정션을 따라가므로 워크스페이스 밖 파일이 결과에 섞였다.
/// 예: 워크스페이스 안 폴더에 .ssh 로 향하는 정션이 있으면 compress_files 가 그 내용을 zip 에 담았다
/// (그리고 에이전트는 mklink /j 로 그 정션을 스스로 만들 수 있다).
///
/// 링크를 "재검증"이 아니라 "미traversal"로 막는 이유: 링크를 따라가지 않으면 하위 트리를 벗어날 수 없으므로
/// 경로별 재검증(파일마다 링크 해석 syscall)보다 싸고 확실하다.
///
/// 한계: 하드링크(mklink /H)는 재분석 지점이 아니라 경로만으로 구분되지 않는다. 경로 기반 검사로는
/// 막을 수 없는 종류이며, 이 헬퍼의 범위 밖이다.
/// </summary>
internal static class SafeFileWalk
{
    /// <summary>
    /// <paramref name="root"/> 하위의 파일을 열거한다. 링크(파일/디렉토리 모두)와 읽을 수 없는 디렉토리는
    /// 건너뛰고 <paramref name="skipped"/> 에 담는다 — 호출자는 이를 사용자에게 보고해야 한다.
    /// 조용히 빠뜨리면 "전부 압축됐다"고 오해하게 된다.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string root, ICollection<string> skipped, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            string[] files;
            string[] subDirs;
            try
            {
                files = Directory.GetFiles(dir);
                subDirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add(dir);
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;   // 열거 중 사라짐 — 정상 상황.
            }

            foreach (var file in files)
            {
                if (IsLink(file)) { skipped.Add(file); continue; }
                yield return file;
            }

            foreach (var sub in subDirs)
            {
                if (IsLink(sub)) { skipped.Add(sub); continue; }
                pending.Push(sub);
            }
        }
    }

    /// <summary>
    /// 재분석 지점(정션/심볼릭 링크) 여부. 판단할 수 없으면 링크로 간주한다 —
    /// 속성을 못 읽는 대상을 따라가는 것보다 건너뛰는 편이 안전하다.
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }
}
