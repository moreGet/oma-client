using System.Linq;
using OhMyAgent.AiAgent.Client.Models.Mcp;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// <see cref="ScriptExecutor.ResolveShell"/> OS 라우팅 검증.
/// isWindows 를 명시 주입하므로 net10.0-windows 러너에서도 Linux 분기 산출물을 검증할 수 있다.
/// </summary>
public sealed class ScriptExecutorShellRoutingTests
{
    // ── Windows 분기: 바이트 불변 ──

    [Fact]
    public void Windows_PowerShell_UsesPowershellExeWithCommandArgLine()
    {
        var inv = ScriptExecutor.ResolveShell(ScriptType.PowerShell, "Get-ChildItem", isWindows: true);

        Assert.Equal("powershell.exe", inv.FileName);
        Assert.NotNull(inv.ArgLine);
        Assert.Contains("-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command", inv.ArgLine!);
        Assert.Contains("Get-ChildItem", inv.ArgLine!);
        Assert.Empty(inv.ArgList);
    }

    [Fact]
    public void Windows_Cmd_UsesCmdExeWithSlashC()
    {
        var inv = ScriptExecutor.ResolveShell(ScriptType.Cmd, "dir", isWindows: true);

        Assert.Equal("cmd.exe", inv.FileName);
        Assert.NotNull(inv.ArgLine);
        Assert.StartsWith("/c \"", inv.ArgLine!);
        Assert.Contains("dir", inv.ArgLine!);
        Assert.Empty(inv.ArgList);
    }

    [Fact]
    public void Windows_PowerShell_AppliesQuoteEscaping()
    {
        var inv = ScriptExecutor.ResolveShell(ScriptType.PowerShell, "Write-Output \"hi\"", isWindows: true);

        // 기존 Escape(따옴표 → \") 가 그대로 적용되어야 한다(바이트 불변).
        Assert.Contains("\\\"hi\\\"", inv.ArgLine!);
    }

    // ── Linux 분기: 세 타입 모두 /bin/bash -c, argv 직접 전달 ──

    [Theory]
    [InlineData(ScriptType.PowerShell)]
    [InlineData(ScriptType.Cmd)]
    [InlineData(ScriptType.Bash)]
    public void Linux_AllTypes_RouteToBinBashWithArgList(ScriptType type)
    {
        const string script = "ls -la";
        var inv = ScriptExecutor.ResolveShell(type, script, isWindows: false);

        Assert.Equal("/bin/bash", inv.FileName);
        Assert.Null(inv.ArgLine);                        // Linux 는 ArgLine 미사용
        Assert.Equal(new[] { "-c", script }, inv.ArgList.ToArray());
    }

    [Fact]
    public void Linux_PassesRawScriptWithoutEscaping()
    {
        // Linux 는 argv 직접 전달 → 따옴표·$·백틱 이스케이프 미적용(원본 보존).
        const string script = "echo \"$HOME\" `date`";
        var inv = ScriptExecutor.ResolveShell(ScriptType.Bash, script, isWindows: false);

        Assert.Equal(new[] { "-c", script }, inv.ArgList.ToArray());
        Assert.DoesNotContain("\\\"", inv.ArgList[1]);   // 이스케이프가 삽입되지 않았다
    }
}
