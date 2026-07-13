using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// 파일·폴더들을 하나의 zip 으로 압축한다. 모든 경로는 워크스페이스 샌드박스로 검증된다(BCL, 폐쇄망 적합).
/// </summary>
public sealed class CompressFilesTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{
          "sources":{"type":"array","items":{"type":"string"},"description":"압축할 파일/폴더 경로 목록(워크스페이스 내부)"},
          "destination":{"type":"string","description":"생성할 .zip 파일 경로"},
          "overwrite":{"type":"boolean","description":"대상 zip 이 있으면 덮어쓸지(기본 false)"}
        },"required":["sources","destination"]}
        """);

    public string Name => "compress_files";
    public string Description => "Compress files and/or folders into a single .zip archive. All paths are sandboxed to the workspace. Folders are added recursively (folder name preserved).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (!args.TryGetProperty("sources", out var srcArr) || srcArr.ValueKind != JsonValueKind.Array || srcArr.GetArrayLength() == 0)
            return ToolResult.Fail("sources 배열(압축할 파일/폴더)이 필요합니다.");
        var destination = ToolSchemas.GetString(args, "destination");
        if (string.IsNullOrWhiteSpace(destination))
            return ToolResult.Fail("destination(.zip 경로)이 필요합니다.");
        var overwrite = ToolSchemas.GetBool(args, "overwrite");

        var sources = new List<string>();
        foreach (var el in srcArr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                sources.Add(el.GetString()!);

        var destFull = ctx.Workspace.ResolvePath(destination);
        if (File.Exists(destFull) && !overwrite)
            return ToolResult.Fail($"이미 존재합니다(overwrite=false): {destination}");

        return await Task.Run(() =>
        {
            var added = new List<string>();
            var missing = new List<string>();

            var parent = Path.GetDirectoryName(destFull);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            using (var zipStream = new FileStream(destFull, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var src in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    var srcFull = ctx.Workspace.ResolvePath(src);

                    if (Directory.Exists(srcFull))
                    {
                        var baseDir = Path.GetDirectoryName(srcFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? srcFull;
                        foreach (var file in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories))
                        {
                            ct.ThrowIfCancellationRequested();
                            var entry = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                            archive.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
                            added.Add(entry);
                        }
                    }
                    else if (File.Exists(srcFull))
                    {
                        var entry = Path.GetFileName(srcFull);
                        archive.CreateEntryFromFile(srcFull, entry, CompressionLevel.Optimal);
                        added.Add(entry);
                    }
                    else
                    {
                        missing.Add(src);
                    }
                }
            }

            if (added.Count == 0)
            {
                if (File.Exists(destFull)) File.Delete(destFull);   // 빈 아카이브 남기지 않음
                return ToolResult.Fail($"압축할 대상을 찾지 못했습니다: {string.Join(", ", missing)}");
            }

            var size = new FileInfo(destFull).Length;
            return ToolResult.Json(new
            {
                destination,
                entries = added.Count,
                bytes = size,
                missing = missing.Count == 0 ? null : missing
            });
        }, ct).ConfigureAwait(false);
    }
}
