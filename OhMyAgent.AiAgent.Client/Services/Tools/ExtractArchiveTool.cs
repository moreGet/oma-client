using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// zip 아카이브를 워크스페이스 내 폴더로 해제한다. 경로 탈출(zip-slip)·zip 폭탄·정션 우회를 차단한다.
/// </summary>
public sealed class ExtractArchiveTool : ITool
{
    // zip 폭탄 방어. 종전에는 해제 크기에 상한이 전혀 없어 42KB 아카이브가 수 GB 로 부풀어 디스크를 채울 수 있었다.
    // 압축 헤더의 선언 크기(Entry.Length)는 공격자가 조작할 수 있으므로 믿지 않고, 실제 기록 바이트를 세며 끊는다.
    private const long MaxTotalBytes = 512L * 1024 * 1024;   // 해제 총량 512MB
    private const int  MaxEntries    = 20_000;               // 항목 수(수십만 개 빈 파일 공격 방어)
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{
          "archive":{"type":"string","description":"해제할 .zip 파일 경로(워크스페이스 내부)"},
          "destination":{"type":"string","description":"해제 대상 폴더(없으면 생성)"},
          "overwrite":{"type":"boolean","description":"기존 파일 덮어쓸지(기본 false)"}
        },"required":["archive","destination"]}
        """);

    public string Name => "extract_archive";
    public string Description => "Extract a .zip archive into a folder inside the workspace. Path-traversal (zip-slip) is blocked and the destination is sandboxed.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var archive = ToolSchemas.GetString(args, "archive");
        if (string.IsNullOrWhiteSpace(archive))
            return ToolResult.Fail("archive(.zip 경로)가 필요합니다.");
        var destination = ToolSchemas.GetString(args, "destination");
        if (string.IsNullOrWhiteSpace(destination))
            return ToolResult.Fail("destination(해제 폴더)이 필요합니다.");
        var overwrite = ToolSchemas.GetBool(args, "overwrite");

        var archiveFull = ctx.Workspace.ResolvePath(archive);
        if (!File.Exists(archiveFull))
            return ToolResult.Fail($"아카이브가 존재하지 않습니다: {archive}");
        var destFull = ctx.Workspace.ResolvePath(destination);

        return await Task.Run(() =>
        {
            Directory.CreateDirectory(destFull);
            try
            {
                return ExtractBounded(zipPath: archiveFull, destRoot: destFull, overwrite: overwrite,
                                      archive: archive!, destination: destination!, ctx: ctx, ct: ct);
            }
            catch (InvalidDataException)
            {
                return ToolResult.Fail($"유효한 zip 아카이브가 아닙니다: {archive}");
            }
            catch (IOException ex) when (!overwrite)
            {
                return ToolResult.Fail($"대상에 이미 파일이 있습니다(overwrite=false): {ex.Message}");
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 항목을 직접 순회하며 해제한다. ZipFile.ExtractToDirectory 를 쓰지 않는 이유:
    /// zip-slip 은 막아주지만 해제 총량에 상한이 없고(zip 폭탄), 대상 폴더 아래 정션이 있으면
    /// 그 너머로 쓰는 것도 막지 못한다. 여기서는 항목마다 경로를 재검증하고 실제 기록량을 세며 끊는다.
    /// </summary>
    private static ToolResult ExtractBounded(
        string zipPath, string destRoot, bool overwrite,
        string archive, string destination, ToolContext ctx, CancellationToken ct)
    {
        var destPrefix = Path.TrimEndingDirectorySeparator(destRoot) + Path.DirectorySeparatorChar;
        long written = 0;
        var files = 0;

        using var zip = ZipFile.OpenRead(zipPath);

        if (zip.Entries.Count > MaxEntries)
            return ToolResult.Fail($"아카이브 항목이 너무 많습니다(최대 {MaxEntries}개): {zip.Entries.Count}개");

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var target = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));

            // 1) zip-slip: 항목 이름의 ../ 로 대상 폴더를 벗어나는 것 차단(사전적).
            if (!target.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail($"아카이브 항목이 대상 폴더를 벗어납니다: {entry.FullName}");

            // 2) 링크 우회: 대상 폴더 아래에 미리 심어둔 정션을 타고 나가는 것 차단(링크 해석).
            //    IsInsideWorkspace 는 존재하는 조상까지 링크를 해석하므로 아직 없는 파일에도 유효하다.
            if (!ctx.Workspace.IsInsideWorkspace(target))
                return ToolResult.Fail($"아카이브 항목의 실제 경로가 작업 디렉토리를 벗어납니다: {entry.FullName}");

            // 디렉토리 항목(이름이 비어 있음)은 폴더만 만든다.
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (File.Exists(target) && !overwrite)
                return ToolResult.Fail($"대상에 이미 파일이 있습니다(overwrite=false): {entry.FullName}");

            using (var src = entry.Open())
            using (var dst = new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew,
                                            FileAccess.Write, FileShare.None))
            {
                // 선언 크기를 믿지 않고 실제로 흘러나온 바이트를 센다.
                var buffer = new byte[81920];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    written += read;
                    if (written > MaxTotalBytes)
                    {
                        dst.Dispose();
                        TryDelete(target);
                        return ToolResult.Fail(
                            $"해제 총량이 상한({MaxTotalBytes / (1024 * 1024)}MB)을 초과했습니다. zip 폭탄일 수 있어 중단했습니다.");
                    }

                    dst.Write(buffer, 0, read);
                }
            }

            files++;
        }

        return ToolResult.Json(new { archive, destination, files, bytes = written });
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 정리 실패는 무시 — 상한 초과 보고가 우선. */ }
    }
}
