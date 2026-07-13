using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// zip 아카이브를 워크스페이스 내 폴더로 해제한다. 경로 탈출(zip-slip)은 BCL 이 차단하며 대상 폴더도 샌드박스로 검증된다.
/// </summary>
public sealed class ExtractArchiveTool : ITool
{
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
                int count;
                using (var zip = ZipFile.OpenRead(archiveFull))
                    count = zip.Entries.Count(e => !string.IsNullOrEmpty(e.Name));   // 디렉토리 항목 제외
                // .NET 의 ExtractToDirectory 는 대상 밖으로 벗어나는 항목(zip-slip)을 차단한다.
                ZipFile.ExtractToDirectory(archiveFull, destFull, overwriteFiles: overwrite);
                return ToolResult.Json(new { archive, destination, files = count });
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
}
