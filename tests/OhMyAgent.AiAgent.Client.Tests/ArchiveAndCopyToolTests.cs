using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// compress_files / copy 의 샌드박스 경계.
///
/// ResolvePath 는 "주어진 경로 하나"만 검증하므로, 하위 트리를 재귀 열거하는 이 두 도구는
/// 정션을 따라가 워크스페이스 밖 내용을 zip 에 담거나(유출) 워크스페이스 안으로 복사해 넣을 수 있었다.
/// 실제 정션을 만들어 검증한다 — 목으로는 재현되지 않는 종류의 버그다.
/// </summary>
public sealed class ArchiveAndCopyToolTests : IDisposable
{
    private const string SecretContent = "PRIVATE-KEY-DO-NOT-LEAK";

    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _outside;
    private readonly ToolContext _ctx;

    public ArchiveAndCopyToolTests()
    {
        _tempRoot  = Path.Combine(Path.GetTempPath(), "omg-tool-tests", Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_tempRoot, "workspace");
        _outside   = Path.Combine(_tempRoot, "outside");

        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "id_rsa"), SecretContent);

        var workspace = new WorkspaceContext(new FakeSettingsService());
        workspace.SetRoot(_workspace);
        _ctx = new ToolContext(workspace, PermissionMode.FullAuto);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* 정션 잔존 가능 — 임시 폴더라 무시. */ }
    }

    /// <summary>mklink /J — 관리자 권한 불필요(= 에이전트가 run_command 로 직접 만들 수 있다).</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit(10_000);
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // ── compress_files ──

    [Fact]
    public async Task Compress_DoesNotFollowJunctionOutOfWorkspace()
    {
        // <workspace>/data/ 안에 바깥으로 향하는 정션을 심는다.
        var data = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "ok.txt"), "safe");

        if (!TryCreateJunction(Path.Combine(data, "escape"), _outside))
            return;   // 정션 미지원 환경 → 검증 불가.

        var result = await new CompressFilesTool().ExecuteAsync(
            Args("""{"sources":["data"],"destination":"out.zip"}"""), _ctx);

        Assert.False(result.IsError, result.Content);

        var zipPath = Path.Combine(_workspace, "out.zip");
        using var archive = ZipFile.OpenRead(zipPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        // 워크스페이스 안 파일은 담기고, 정션 너머 비밀은 담기지 않아야 한다.
        Assert.Contains("data/ok.txt", names);
        Assert.DoesNotContain(names, n => n.Contains("id_rsa", StringComparison.OrdinalIgnoreCase));

        // 빠뜨린 사실은 조용히 넘어가지 않고 보고되어야 한다.
        Assert.Contains("escape", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compress_ArchivesOrdinaryTreeFully()
    {
        var data = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(Path.Combine(data, "sub"));
        File.WriteAllText(Path.Combine(data, "a.txt"), "a");
        File.WriteAllText(Path.Combine(data, "sub", "b.txt"), "b");

        var result = await new CompressFilesTool().ExecuteAsync(
            Args("""{"sources":["data"],"destination":"out.zip"}"""), _ctx);

        Assert.False(result.IsError, result.Content);

        using var archive = ZipFile.OpenRead(Path.Combine(_workspace, "out.zip"));
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("data/a.txt", names);
        Assert.Contains("data/sub/b.txt", names);   // 링크가 아닌 하위 디렉토리는 정상 재귀
    }

    [Fact]
    public async Task Compress_RejectsSourceOutsideWorkspace()
    {
        await Assert.ThrowsAsync<AgentException>(() =>
            new CompressFilesTool().ExecuteAsync(
                Args($$"""{"sources":[{{JsonSerializer.Serialize(Path.Combine(_outside, "id_rsa"))}}],"destination":"out.zip"}"""),
                _ctx));
    }

    [Fact]
    public async Task Compress_FailsWhenOnlyLinksMatched()
    {
        var data = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(data);

        if (!TryCreateJunction(Path.Combine(data, "escape"), _outside))
            return;

        var result = await new CompressFilesTool().ExecuteAsync(
            Args("""{"sources":["data"],"destination":"out.zip"}"""), _ctx);

        // 담을 게 하나도 없으면 빈 zip 을 남기지 않고 실패해야 한다.
        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(_workspace, "out.zip")));
    }

    // ── copy ──

    [Fact]
    public async Task Copy_DoesNotPullOutsideContentIntoWorkspace()
    {
        var data = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "ok.txt"), "safe");

        if (!TryCreateJunction(Path.Combine(data, "escape"), _outside))
            return;

        var result = await new CopyTool().ExecuteAsync(
            Args("""{"source":"data","destination":"copied"}"""), _ctx);

        Assert.False(result.IsError, result.Content);

        var copied = Path.Combine(_workspace, "copied");
        Assert.True(File.Exists(Path.Combine(copied, "ok.txt")));

        // 정션 너머 비밀이 워크스페이스 안으로 끌려들어오면 안 된다.
        Assert.False(File.Exists(Path.Combine(copied, "escape", "id_rsa")));
        Assert.Empty(Directory.GetFiles(copied, "id_rsa", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Copy_CopiesOrdinaryTreeFully()
    {
        var data = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(Path.Combine(data, "sub"));
        File.WriteAllText(Path.Combine(data, "a.txt"), "a");
        File.WriteAllText(Path.Combine(data, "sub", "b.txt"), "b");

        var result = await new CopyTool().ExecuteAsync(
            Args("""{"source":"data","destination":"copied"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        Assert.True(File.Exists(Path.Combine(_workspace, "copied", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_workspace, "copied", "sub", "b.txt")));
    }

    [Fact]
    public async Task Copy_RejectsSourceOutsideWorkspace()
    {
        await Assert.ThrowsAsync<AgentException>(() =>
            new CopyTool().ExecuteAsync(
                Args($$"""{"source":{{JsonSerializer.Serialize(_outside)}},"destination":"copied"}"""),
                _ctx));
    }

    // ── SafeFileWalk ──

    [Fact]
    public void SafeFileWalk_SkipsJunctionAndReportsIt()
    {
        var data = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "ok.txt"), "safe");

        var link = Path.Combine(data, "escape");
        if (!TryCreateJunction(link, _outside))
            return;

        var skipped = new List<string>();
        var files = SafeFileWalk.EnumerateFiles(data, skipped, default).ToList();

        Assert.Single(files);
        Assert.EndsWith("ok.txt", files[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(skipped, s => s.EndsWith("escape", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SafeFileWalk_IsLink_FalseForOrdinaryEntries()
    {
        var file = Path.Combine(_workspace, "plain.txt");
        File.WriteAllText(file, "x");

        Assert.False(SafeFileWalk.IsLink(file));
        Assert.False(SafeFileWalk.IsLink(_workspace));
    }

    [Fact]
    public void SafeFileWalk_IsLink_TrueWhenUndecidable()
    {
        // 존재하지 않는 경로 → 속성을 못 읽는다 → 안전 우선으로 링크 취급(따라가지 않음).
        Assert.True(SafeFileWalk.IsLink(Path.Combine(_workspace, "no-such-file")));
    }
}
