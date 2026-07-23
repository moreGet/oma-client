using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// grep / glob 의 샌드박스 경계.
///
/// 두 도구는 ResolvePath 로 baseDir "하나"만 검증한 뒤 하위 트리를 직접 순회했기 때문에,
/// 워크스페이스 안에 바깥을 가리키는 정션이 있으면 그 너머 파일을 읽어 결과로 돌려줬다.
/// compress_files/copy 는 같은 문제로 이미 고쳐졌지만(ArchiveAndCopyToolTests) 이 둘은 누락돼 있었다.
///
/// 특히 위험한 조합이었다: 둘 다 ToolRisk.ReadOnly 라 Manual 모드에서도 승인 없이 실행되고,
/// 서브에이전트 허용 목록에도 들어 있다. 즉 사용자 눈에 띄지 않게 파일 내용이 새어나갈 수 있었다.
/// 목으로는 재현되지 않으므로 실제 정션을 만들어 검증한다.
/// </summary>
public sealed class SearchToolSandboxTests : IDisposable
{
    private const string SecretContent = "PRIVATE-KEY-DO-NOT-LEAK";

    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly string _outside;
    private readonly ToolContext _ctx;

    public SearchToolSandboxTests()
    {
        _tempRoot  = Path.Combine(Path.GetTempPath(), "omg-search-tests", Guid.NewGuid().ToString("N"));
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

    /// <summary>워크스페이스 안에 바깥으로 향하는 정션을 심고, 안쪽에도 같은 문자열을 하나 둔다.</summary>
    private void PlantJunctionAndDecoy(out bool supported)
    {
        var data = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "ok.txt"), SecretContent);   // 안쪽 파일 — 이건 찾혀야 정상
        supported = TryCreateJunction(Path.Combine(data, "escape"), _outside);
    }

    [Fact]
    public async Task Grep_DoesNotFollowJunctionOutOfWorkspace()
    {
        PlantJunctionAndDecoy(out var supported);
        if (!supported) return;   // 정션 미지원 환경 → 검증 불가.

        var result = await new GrepTool().ExecuteAsync(
            Args($$"""{"pattern":"{{SecretContent}}"}"""), _ctx);

        Assert.False(result.IsError, result.Content);

        // 워크스페이스 안 파일은 검색되어야 한다(도구가 그냥 죽은 게 아님을 확인).
        Assert.Contains("ok.txt", result.Content, StringComparison.OrdinalIgnoreCase);

        // 정션 너머 파일은 결과에 없어야 한다.
        Assert.DoesNotContain("id_rsa", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Glob_DoesNotFollowJunctionOutOfWorkspace()
    {
        PlantJunctionAndDecoy(out var supported);
        if (!supported) return;

        var result = await new GlobTool().ExecuteAsync(
            Args("""{"pattern":"**/*"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("ok.txt", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id_rsa", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>정션이 없으면 평소대로 하위 디렉토리를 끝까지 훑어야 한다(과잉 차단 방지).</summary>
    [Fact]
    public async Task Grep_StillDescendsRealSubdirectories()
    {
        var deep = Path.Combine(_workspace, "a", "b", "c");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "deep.txt"), "NEEDLE-IN-DEEP-DIR");

        var result = await new GrepTool().ExecuteAsync(
            Args("""{"pattern":"NEEDLE-IN-DEEP-DIR"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("deep.txt", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}
