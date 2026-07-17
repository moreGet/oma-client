using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// "찾지 못함"이 막다른 길이 아니라 회복 가능한 실패여야 한다.
/// 모델이 다음 수를 정할 수 있으려면 실패 결과에 후보와 대안이 실려 있어야 한다.
/// </summary>
public sealed class NotFoundHelpTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public NotFoundHelpTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "omg-notfound-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "junk"));

        File.WriteAllText(Path.Combine(_root, "src", "MainWindow.xaml.cs"), "class MainWindow {}");
        File.WriteAllText(Path.Combine(_root, "src", "SecurityValidator.cs"), "class SecurityValidator {}");
        // 제외 대상 폴더에 동명 파일 — 후보를 오염시키면 안 된다.
        File.WriteAllText(Path.Combine(_root, "node_modules", "junk", "MainWindow.xaml.cs"), "noise");

        var ws = new WorkspaceContext(new FakeSettingsService());
        ws.SetRoot(_root);
        _ctx = new ToolContext(ws, PermissionMode.FullAuto);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 임시 폴더 — 무시. */ }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ReadFile_SuggestsSimilarNameOnTypo()
    {
        // 오타 — 종전이면 "파일이 존재하지 않습니다"로 끝나 모델에게 남는 정보가 없었다.
        var result = await new ReadFileTool().ExecuteAsync(
            Args("""{"path":"src/SecurityValidatr.cs"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.Contains("SecurityValidator.cs", result.Content);
    }

    [Fact]
    public async Task ReadFile_SuggestsFileInDifferentDirectory()
    {
        // 사용자가 위치를 잘못 안 경우 — 이름은 맞다.
        var result = await new ReadFileTool().ExecuteAsync(
            Args("""{"path":"MainWindow.xaml.cs"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.Contains("src/MainWindow.xaml.cs", result.Content);
    }

    [Fact]
    public async Task ReadFile_CandidatesExcludeIgnoredDirectories()
    {
        var result = await new ReadFileTool().ExecuteAsync(
            Args("""{"path":"MainWindow.xaml.cs"}"""), _ctx);

        // node_modules 안의 동명 파일이 후보로 올라오면 모델을 엉뚱한 곳으로 보낸다.
        Assert.DoesNotContain("node_modules", result.Content);
    }

    [Fact]
    public async Task ReadFile_SuggestsContentSearchWhenNothingSimilar()
    {
        var result = await new ReadFileTool().ExecuteAsync(
            Args("""{"path":"zzzzqqqqwwww.txt"}"""), _ctx);

        Assert.True(result.IsError);
        // 후보가 없으면 최소한 다음 수(내용 검색/되묻기)를 제시해야 한다.
        Assert.Contains("grep", result.Content);
        Assert.Contains("되묻", result.Content);
    }

    [Fact]
    public async Task Glob_ZeroMatchesCarriesHintNotSilence()
    {
        var result = await new GlobTool().ExecuteAsync(
            Args("""{"pattern":"**/SecurityValidatr*"}"""), _ctx);

        Assert.False(result.IsError);   // 0건은 오류가 아니다
        using var doc = JsonDocument.Parse(result.Content);
        var hint = doc.RootElement.GetProperty("hint").GetString();

        Assert.NotNull(hint);
        Assert.Contains("SecurityValidator.cs", hint);   // 닮은 이름을 알려줘야 한다
    }

    [Fact]
    public async Task Glob_SuccessfulMatchHasNoHint()
    {
        var result = await new GlobTool().ExecuteAsync(
            Args("""{"pattern":"**/*.cs"}"""), _ctx);

        using var doc = JsonDocument.Parse(result.Content);
        Assert.False(doc.RootElement.TryGetProperty("hint", out _));   // 찾았으면 잔소리 금지
        Assert.True(doc.RootElement.GetProperty("files").GetArrayLength() > 0);
    }
}
