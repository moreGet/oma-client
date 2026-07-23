using OhMyAgent.AiAgent.Client.Models.Mcp;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 셸 라벨 우회 회귀(스펙 §3-C). net10.0-windows 러너에서 RunCommandTool.ResolveValidationType 의
/// 양쪽 OS 분기를 프로세스 스폰 없이 잠근다 — QA 가 남긴 "Windows 러너에서 Linux 경로 직접 검증 불가"
/// 한계를 순수 함수 합성으로 해소.
/// </summary>
public class RunCommandToolValidationTypeTests
{
    // ── Windows 는 요청 셸 그대로(기존 동작 불변) ──
    [Theory]
    [InlineData(ScriptType.Cmd)]
    [InlineData(ScriptType.PowerShell)]
    [InlineData(ScriptType.Bash)]
    public void Windows_keeps_requested_shell(ScriptType requested)
        => Assert.Equal(requested, RunCommandTool.ResolveValidationType(requested, isWindows: true));

    // ── 비-Windows 는 항상 Bash 규칙(라벨 무관) ──
    [Theory]
    [InlineData(ScriptType.Cmd)]
    [InlineData(ScriptType.PowerShell)]
    [InlineData(ScriptType.Bash)]
    public void Linux_always_resolves_to_bash(ScriptType requested)
        => Assert.Equal(ScriptType.Bash, RunCommandTool.ResolveValidationType(requested, isWindows: false));

    // ── 합성 회귀: cmd/powershell 라벨로 bash 위험 명령을 위장해도 Bash 규칙으로 차단 ──
    [Theory]
    [InlineData("rm -rf /", ScriptType.Cmd)]
    [InlineData("cat /etc/shadow", ScriptType.PowerShell)]
    [InlineData("dd of=/dev/sda", ScriptType.Cmd)]
    public void Label_bypass_is_blocked_on_linux(string command, ScriptType requestedLabel)
    {
        var type = RunCommandTool.ResolveValidationType(requestedLabel, isWindows: false);
        Assert.Equal(ScriptType.Bash, type);

        var result = SecurityValidator.Validate(command, type);
        Assert.False(result.IsValid);
    }

    // ── Windows 대조: cmd 라벨은 Cmd 타입으로 남는다(Bash 규칙 강제 안 됨) ──
    [Fact]
    public void Windows_cmd_label_stays_cmd_type()
    {
        var type = RunCommandTool.ResolveValidationType(ScriptType.Cmd, isWindows: true);
        Assert.Equal(ScriptType.Cmd, type);
        Assert.NotEqual(ScriptType.Bash, type);
    }
}
