using System;
using System.Diagnostics;
using OhMyAgent.AiAgent.Client.Models.Mcp;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class SecurityValidatorTests
{
    // ── 회귀: 레지스트리 규칙이 과다 이스케이프로 한 번도 발동하지 않던 버그 ──
    //
    // 버그 당시 패턴: @"\breg\s+(delete|add)\b.*HKLM\\\\SYSTEM"
    // 검증 엔진은 백슬래시 2개(HKLM\\SYSTEM)를 찾았지만 실제 명령엔 1개뿐이라 항상 미매치였다.
    // 이 테스트가 실패하면 그 이스케이프가 되돌아온 것이다.

    [Theory]
    [InlineData(@"reg delete HKLM\SYSTEM\CurrentControlSet\Services\Foo /f")]
    [InlineData(@"reg add HKLM\SYSTEM\CurrentControlSet\Control /v Foo /d 1")]
    [InlineData(@"REG DELETE HKLM\SYSTEM\Setup")]           // 대소문자 무시 확인
    public void Validate_BlocksRegistryTampering(string script)
    {
        var result = SecurityValidator.Validate(script, ScriptType.Cmd);

        Assert.False(result.IsValid);
        Assert.Contains("레지스트리", result.Reason);
    }

    [Theory]
    [InlineData("shutdown /s /t 0")]
    [InlineData("format c:")]
    [InlineData("rmdir /s /q C:\\temp")]
    [InlineData("del /f important.txt")]
    public void Validate_BlocksDestructiveCommands(string script)
    {
        Assert.False(SecurityValidator.Validate(script, ScriptType.Cmd).IsValid);
    }

    [Theory]
    [InlineData("Remove-Item -Recurse -Force C:\\data")]
    [InlineData("Invoke-Expression $payload")]
    [InlineData("Set-ExecutionPolicy Bypass")]
    [InlineData("Stop-Computer")]
    public void Validate_BlocksDangerousPowerShell(string script)
    {
        Assert.False(SecurityValidator.Validate(script, ScriptType.PowerShell).IsValid);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"type C:\Program Files\app\config.ini")]
    [InlineData("%SystemRoot%\\notepad.exe")]
    public void Validate_BlocksSystemPaths(string script)
    {
        Assert.False(SecurityValidator.Validate(script, ScriptType.Cmd).IsValid);
    }

    [Theory]
    [InlineData("dir")]
    [InlineData("echo hello")]
    [InlineData("git status")]
    [InlineData(@"reg query HKCU\Software")]                // query 는 변조가 아니므로 허용
    [InlineData(@"reg delete HKCU\Software\MyApp")]         // HKLM\SYSTEM 이 아니면 허용
    public void Validate_AllowsBenignCommands(string script)
    {
        var result = SecurityValidator.Validate(script, ScriptType.Cmd);

        Assert.True(result.IsValid, $"차단되면 안 되는 명령이 차단됨: {result.Reason}");
    }

    [Fact]
    public void Validate_RejectsEmptyScript()
    {
        Assert.False(SecurityValidator.Validate("   ", ScriptType.Cmd).IsValid);
    }

    [Fact]
    public void Validate_RejectsOversizedScript()
    {
        var huge = new string('a', 65537);

        Assert.False(SecurityValidator.Validate(huge, ScriptType.Cmd).IsValid);
    }

    // ── 백트래킹 부하 ──
    //
    // 현재 내장 패턴은 `.*X.*Y` 형태뿐이라 중첩 수량자((a+)+ 등)가 없고, 백트래킹이 지수가 아니다.
    // 즉 지금은 ReDoS가 성립하지 않으며 아래 입력도 수십 ms 에 끝난다(그렇게 측정됐다).
    // 그럼에도 내장 패턴에 매치 타임아웃을 건 이유는, 향후 누군가 중첩 수량자 패턴을 추가했을 때
    // 도구 호출이 코어를 물고 멈추는 대신 "차단"으로 안전하게 무너지게 하기 위함이다.
    //
    // 이 테스트는 최악 입력이 상한 시간 안에 끝나는지를 지킨다 — 위험한 패턴이 추가되면 여기서 걸린다.
    [Fact]
    public void Validate_CompletesQuicklyOnAdversarialInput()
    {
        var padded = "Remove-Item " + new string(' ', 60_000) + "!";

        var sw = Stopwatch.StartNew();
        var result = SecurityValidator.Validate(padded, ScriptType.PowerShell);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"백트래킹 폭주 의심 — {sw.Elapsed.TotalSeconds:F1}초 소요");

        // -Recurse/-Force 가 없으므로 이 입력은 실제로 위험하지 않다 → 통과가 정상이다.
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_StillBlocksDangerousCommandInsideLongScript()
    {
        // 긴 스크립트 속에 숨겨도 잡아야 한다(길이로 검사를 회피할 수 없음).
        var script = new string('\n', 30_000) + "Remove-Item -Recurse -Force C:\\data" + new string('\n', 30_000);

        Assert.False(SecurityValidator.Validate(script, ScriptType.PowerShell).IsValid);
    }
}
