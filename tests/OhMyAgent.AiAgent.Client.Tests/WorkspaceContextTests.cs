using System;
using System.Diagnostics;
using System.IO;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 샌드박스 경계 검증. 실제 파일시스템과 정션(junction)을 만들어 확인한다 —
/// 이 버그의 본질이 "링크 해석을 어느 경로 단계에서 하는가"라서 목으로는 재현되지 않는다.
/// </summary>
public sealed class WorkspaceContextTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _outside;

    public WorkspaceContextTests()
    {
        _tempRoot  = Path.Combine(Path.GetTempPath(), "omg-ws-tests", Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_tempRoot, "workspace");
        _outside   = Path.Combine(_tempRoot, "outside");

        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "id_rsa");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* 정션이 남아 있으면 실패할 수 있다 — 임시 폴더이므로 무시. */ }
    }

    private WorkspaceContext CreateContext()
    {
        var ctx = new WorkspaceContext(new FakeSettingsService());
        ctx.SetRoot(_workspace);
        return ctx;
    }

    /// <summary>mklink /J — 관리자 권한 없이 만들 수 있다(= 에이전트가 run_command 로 직접 만들 수 있다).</summary>
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

    // ── 핵심 회귀 테스트 ──
    //
    // 버그 당시 RealPath 는 "존재하는 첫 경로"에서 멈췄고, 리프(secret.txt)가 존재하므로
    // 리프에만 ResolveLinkTarget 을 호출했다. 파일은 재분석 지점이 아니라 null 이 반환되고
    // 사전적 경로가 그대로 통과 → 정션 뒤의 워크스페이스 밖 파일이 읽혔다.
    //
    // 링크가 "리프일 때"만 막히고 "중간 경로일 때"는 뚫리는 게 이 버그의 핵심이므로,
    // 두 형태를 모두 검증한다.
    [Fact]
    public void ResolvePath_RejectsFileBehindDirectoryJunction()
    {
        var link = Path.Combine(_workspace, "link");
        if (!TryCreateJunction(link, _outside))
            return;   // 정션을 못 만드는 환경(파일시스템 미지원 등) → 검증 불가이므로 건너뛴다.

        var ctx = CreateContext();

        // <workspace>\link\secret.txt → 실제로는 <outside>\secret.txt
        Assert.Throws<AgentException>(() => ctx.ResolvePath(@"link\secret.txt"));
        Assert.False(ctx.IsInsideWorkspace(Path.Combine(link, "secret.txt")));
    }

    [Fact]
    public void ResolvePath_RejectsDirectoryJunctionItself()
    {
        var link = Path.Combine(_workspace, "link");
        if (!TryCreateJunction(link, _outside))
            return;

        var ctx = CreateContext();

        Assert.Throws<AgentException>(() => ctx.ResolvePath("link"));
    }

    [Fact]
    public void ResolvePath_RejectsPathBehindNestedJunction()
    {
        var nested = Path.Combine(_workspace, "a", "b");
        Directory.CreateDirectory(nested);

        var link = Path.Combine(nested, "link");
        if (!TryCreateJunction(link, _outside))
            return;

        var ctx = CreateContext();

        // 링크가 경로 중간 깊숙이 있어도 막혀야 한다.
        Assert.Throws<AgentException>(() => ctx.ResolvePath(@"a\b\link\secret.txt"));
    }

    [Fact]
    public void ResolvePath_RejectsTraversalOutsideRoot()
    {
        var ctx = CreateContext();

        Assert.Throws<AgentException>(() => ctx.ResolvePath(@"..\outside\secret.txt"));
        Assert.Throws<AgentException>(() => ctx.ResolvePath(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public void ResolvePath_AllowsFilesInsideRoot()
    {
        var ctx = CreateContext();
        File.WriteAllText(Path.Combine(_workspace, "ok.txt"), "hi");

        var resolved = ctx.ResolvePath("ok.txt");

        Assert.Equal(Path.Combine(_workspace, "ok.txt"), resolved);
        Assert.True(ctx.IsInsideWorkspace(resolved));
    }

    [Fact]
    public void ResolvePath_AllowsNotYetExistingFileInsideRoot()
    {
        var ctx = CreateContext();

        // write_file 신규 생성 경로 — 아직 없는 파일도 루트 안이면 허용해야 한다.
        var resolved = ctx.ResolvePath(@"sub\new.txt");

        Assert.Equal(Path.Combine(_workspace, "sub", "new.txt"), resolved);
    }

    [Fact]
    public void ResolvePath_EmptyPathReturnsRoot()
    {
        var ctx = CreateContext();

        Assert.Equal(_workspace, ctx.ResolvePath(""));
    }

    [Fact]
    public void SetRoots_AllowsEachActiveRoot()
    {
        var second = Path.Combine(_tempRoot, "workspace2");
        Directory.CreateDirectory(second);

        var ctx = new WorkspaceContext(new FakeSettingsService());
        ctx.SetRoots([_workspace, second]);

        File.WriteAllText(Path.Combine(second, "b.txt"), "b");

        Assert.Equal(_workspace, ctx.Root);   // 첫 항목이 주 루트
        Assert.True(ctx.IsInsideWorkspace(Path.Combine(second, "b.txt")));
        Assert.Throws<AgentException>(() => ctx.ResolvePath(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public void SetRoots_EmptyFallsBackToSingleRoot()
    {
        var ctx = new WorkspaceContext(new FakeSettingsService());
        ctx.SetRoots([]);

        Assert.Single(ctx.Roots);
        Assert.False(string.IsNullOrWhiteSpace(ctx.Root));
    }
}
