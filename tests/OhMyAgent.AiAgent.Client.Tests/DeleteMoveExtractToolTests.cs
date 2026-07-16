using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public sealed class DeleteMoveExtractToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly WorkspaceContext _workspace;
    private readonly ToolContext _ctx;

    public DeleteMoveExtractToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "omg-dme-tests", Guid.NewGuid().ToString("N"));
        _rootA = Path.Combine(_tempRoot, "wsA");
        _rootB = Path.Combine(_tempRoot, "wsB");
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);

        _workspace = new WorkspaceContext(new FakeSettingsService());
        _workspace.SetRoots([_rootA, _rootB]);   // 멀티 루트
        _ctx = new ToolContext(_workspace, PermissionMode.FullAuto);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* 임시 폴더 — 무시. */ }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // ── delete / move: 멀티 루트 ──
    //
    // 종전에는 Workspace.Root(= Roots[0]) 만 비교해, 두 번째 워크스페이스 폴더가 통째로 삭제/이동됐다.

    [Fact]
    public async Task Delete_RejectsSecondaryWorkspaceRoot()
    {
        File.WriteAllText(Path.Combine(_rootB, "important.txt"), "data");

        var result = await new DeleteTool().ExecuteAsync(
            Args($$"""{"path":{{JsonSerializer.Serialize(_rootB)}},"recursive":true}"""), _ctx);

        Assert.True(result.IsError);
        Assert.True(Directory.Exists(_rootB));
        Assert.True(File.Exists(Path.Combine(_rootB, "important.txt")));
    }

    [Fact]
    public async Task Delete_RejectsPrimaryWorkspaceRoot()
    {
        var result = await new DeleteTool().ExecuteAsync(
            Args($$"""{"path":{{JsonSerializer.Serialize(_rootA)}},"recursive":true}"""), _ctx);

        Assert.True(result.IsError);
        Assert.True(Directory.Exists(_rootA));
    }

    [Fact]
    public async Task Delete_AllowsFileInsideRoot()
    {
        var file = Path.Combine(_rootA, "tmp.txt");
        File.WriteAllText(file, "x");

        var result = await new DeleteTool().ExecuteAsync(Args("""{"path":"tmp.txt"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Move_RejectsSecondaryWorkspaceRootAsSource()
    {
        var result = await new MoveTool().ExecuteAsync(
            Args($$"""{"source":{{JsonSerializer.Serialize(_rootB)}},"destination":"moved"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.True(Directory.Exists(_rootB));
    }

    [Fact]
    public async Task Move_RejectsSecondaryWorkspaceRootAsDestination()
    {
        Directory.CreateDirectory(Path.Combine(_rootA, "src"));

        var result = await new MoveTool().ExecuteAsync(
            Args($$"""{"source":"src","destination":{{JsonSerializer.Serialize(_rootB)}},"overwrite":true}"""), _ctx);

        Assert.True(result.IsError);
        Assert.True(Directory.Exists(_rootB));
    }

    // ── extract_archive ──

    /// <summary>선언 크기가 아니라 실제 해제 바이트로 상한을 걸어야 하므로, 잘 압축되는 큰 내용을 만든다.</summary>
    private string MakeZip(string name, string entryName, long uncompressedBytes)
    {
        var zipPath = Path.Combine(_rootA, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var s = entry.Open();

        var chunk = new byte[1024 * 1024];   // 0으로 채운 1MB → 압축률이 극단적으로 높다(zip 폭탄 성질)
        long left = uncompressedBytes;
        while (left > 0)
        {
            var n = (int)Math.Min(chunk.Length, left);
            s.Write(chunk, 0, n);
            left -= n;
        }
        return zipPath;
    }

    [Fact]
    public async Task Extract_RejectsZipBombExceedingTotalCap()
    {
        // 상한(512MB)을 넘는 해제 총량 → 압축 파일 자체는 수백 KB 에 불과하다.
        MakeZip("bomb.zip", "big.bin", 600L * 1024 * 1024);

        var result = await new ExtractArchiveTool().ExecuteAsync(
            Args("""{"archive":"bomb.zip","destination":"out"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.Contains("폭탄", result.Content);

        // 중단 시 부분 파일을 남기지 않아야 한다.
        Assert.False(File.Exists(Path.Combine(_rootA, "out", "big.bin")));
    }

    [Fact]
    public async Task Extract_AllowsOrdinaryArchive()
    {
        var zipPath = Path.Combine(_rootA, "ok.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("docs/hello.txt");
            using var w = new StreamWriter(e.Open(), Encoding.UTF8);
            w.Write("hello");
        }

        var result = await new ExtractArchiveTool().ExecuteAsync(
            Args("""{"archive":"ok.zip","destination":"out"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_rootA, "out", "docs", "hello.txt")));
    }

    [Fact]
    public async Task Extract_RejectsZipSlipEntry()
    {
        var zipPath = Path.Combine(_rootA, "slip.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // 대상 폴더를 벗어나려는 항목명.
            var e = zip.CreateEntry("../../escaped.txt");
            using var w = new StreamWriter(e.Open(), Encoding.UTF8);
            w.Write("pwned");
        }

        var result = await new ExtractArchiveTool().ExecuteAsync(
            Args("""{"archive":"slip.zip","destination":"out"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(_tempRoot, "escaped.txt")));
    }

    [Fact]
    public async Task Extract_RespectsOverwriteFalse()
    {
        var zipPath = Path.Combine(_rootA, "dup.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("a.txt");
            using var w = new StreamWriter(e.Open(), Encoding.UTF8);
            w.Write("new");
        }

        Directory.CreateDirectory(Path.Combine(_rootA, "out"));
        File.WriteAllText(Path.Combine(_rootA, "out", "a.txt"), "original");

        var result = await new ExtractArchiveTool().ExecuteAsync(
            Args("""{"archive":"dup.zip","destination":"out"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.Equal("original", File.ReadAllText(Path.Combine(_rootA, "out", "a.txt")));
    }
}
