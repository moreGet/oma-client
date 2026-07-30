using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 셸 출력 디코딩 회귀.
///
/// cmd/PowerShell 내장 명령은 콘솔 OEM 코드페이지(한국어 Windows = 949)로 바이트를 쓴다.
/// 이를 UTF-8 로 디코딩하면 한글이 U+FFFD 로 깨진다("C 드라이브의 볼륨" → "C ����̺��� ����").
///
/// chcp 65001 접두사로는 고쳐지지 않는다 — 출력이 리다이렉트된 경우 코드페이지 전환이
/// 내장 명령 출력에 반영되지 않는다(실측 확인). 디코딩 쪽을 OEM 으로 맞춰야 한다.
/// </summary>
public sealed class ScriptExecutorEncodingTests
{
    private const char ReplacementChar = '�';

    /// <summary>한국어(949) 등 비UTF 코드페이지 환경에서만 의미 있는 검증.</summary>
    private static bool IsMultiByteOemEnvironment()
        => CultureInfo.CurrentCulture.TextInfo.OEMCodePage is 949 or 932 or 936 or 950;

    [Fact]
    public async Task Cmd_DecodesLocalizedOutputWithoutReplacementChars()
    {
        if (!OperatingSystem.IsWindows() || !IsMultiByteOemEnvironment())
            return;   // 영문/UTF-8 환경 → 검증 대상 아님.

        // `dir` 은 헤더를 OS 언어로 출력한다(한국어: "드라이브의 볼륨", "디렉터리").
        var result = await new ScriptExecutor().ExecuteCmdAsync(@"dir C:\Windows\notepad.exe");

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));

        var broken = result.Stdout.Count(c => c == ReplacementChar);
        Assert.True(broken == 0,
            $"셸 출력 디코딩에 실패한 문자가 {broken}개 있습니다. OEM 코드페이지 디코딩이 깨졌는지 확인하세요.\n"
            + result.Stdout[..Math.Min(300, result.Stdout.Length)]);
    }

    [Fact]
    public async Task PowerShell_RoundTripsNonAsciiText()
    {
        if (!OperatingSystem.IsWindows() || !IsMultiByteOemEnvironment())
            return;

        // 출력한 한글이 그대로 되돌아와야 한다 — 인코딩 왕복 검증.
        var result = await new ScriptExecutor().ExecutePowerShellAsync("Write-Output '한글출력테스트'");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(ReplacementChar, result.Stdout);
        Assert.Contains("한글출력테스트", result.Stdout);
    }

    // ── ResolveConsoleEncoding OS 분기 (순수 메서드 — 전 OS 러너에서 검증) ──

    [Fact]
    public void ResolveConsoleEncoding_Linux_ReturnsUtf8WithoutBom()
    {
        var enc = ScriptExecutor.ResolveConsoleEncoding(isWindows: false);

        Assert.IsType<UTF8Encoding>(enc);
        // BOM 미방출(encoderShouldEmitUTF8Identifier: false).
        Assert.Empty(enc.GetPreamble());
    }

    [Fact]
    public void ResolveConsoleEncoding_Windows_ReturnsUsableEncoding()
    {
        // Windows 분기: OEM 코드페이지 또는 UTF-8 폴백 — 어느 쪽이든 non-null 이고 왕복 가능해야 한다.
        var enc = ScriptExecutor.ResolveConsoleEncoding(isWindows: true);

        Assert.NotNull(enc);
        const string ascii = "hello 123";
        Assert.Equal(ascii, enc.GetString(enc.GetBytes(ascii)));
    }
}
