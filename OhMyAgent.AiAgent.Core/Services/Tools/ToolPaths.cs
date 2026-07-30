using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>파일 도구 공용 경로 처리 — 필수 path 인자 검증·워크스페이스 해석·쓰기 준비.</summary>
internal static class ToolPaths
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// 필수 'path' 인자를 읽어 비어 있지 않은지만 확인한다(워크스페이스 해석 없음).
    /// ResolvePath 는 샌드박스를 벗어나면 예외를 던지므로, 다른 인자 검증을 먼저 끝내야 하는
    /// 도구는 이 단계만 쓰고 해석 시점을 직접 고른다.
    /// </summary>
    public static bool TryGetRequiredPath(
        JsonElement args,
        out string path,
        [NotNullWhen(false)] out ToolResult? error)
    {
        path = ToolSchemas.GetString(args, "path") ?? "";

        if (string.IsNullOrWhiteSpace(path))
        {
            error = ToolResult.Fail("path 가 비어 있습니다.");
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 필수 'path' 인자를 읽어 워크스페이스 절대경로로 해석한다.
    /// 실패하면 error 에 그대로 반환할 ToolResult 가 담긴다.
    /// </summary>
    public static bool TryResolvePath(
        JsonElement args,
        ToolContext ctx,
        out string path,
        out string full,
        [NotNullWhen(false)] out ToolResult? error)
    {
        full = "";
        if (!TryGetRequiredPath(args, out path, out error))
            return false;

        full = ctx.Workspace.ResolvePath(path);
        return true;
    }

    /// <summary><see cref="TryResolvePath"/> 에 더해 대상이 실제 파일로 존재하는지까지 확인한다.</summary>
    public static bool TryResolveExistingFile(
        JsonElement args,
        ToolContext ctx,
        out string path,
        out string full,
        [NotNullWhen(false)] out ToolResult? error)
    {
        if (!TryResolvePath(args, ctx, out path, out full, out error))
            return false;

        if (!File.Exists(full))
        {
            error = ToolResult.Fail($"파일이 존재하지 않습니다: {path}");
            return false;
        }

        return true;
    }

    /// <summary>대상 파일의 부모 디렉터리를 준비한다(이미 있으면 무동작).</summary>
    public static void EnsureParentDirectory(string full)
    {
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>BOM 없는 UTF-8 로 기록하고 기록한 바이트 수를 돌려준다.</summary>
    public static async Task<int> WriteUtf8NoBomAsync(string full, string content, CancellationToken ct)
    {
        var bytes = Utf8NoBom.GetBytes(content);
        await File.WriteAllBytesAsync(full, bytes, ct).ConfigureAwait(false);
        return bytes.Length;
    }
}
