using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// System.Diagnostics.Process 기반 PowerShell / CMD 실행기.
/// 동시 실행 프로세스 수를 SemaphoreSlim 으로 제한.
/// </summary>
public class ScriptExecutor : IScriptExecutor
{
    private const int MaxConcurrency = 4;
    private readonly SemaphoreSlim _concurrencyLimit = new(MaxConcurrency, MaxConcurrency);

    /// <summary>
    /// 리다이렉트된 셸 출력을 디코딩할 인코딩 — 시스템 <b>OEM 코드페이지</b>(한국어 Windows = 949).
    ///
    /// 왜 UTF-8 이 아닌가: cmd/PowerShell 의 내장 명령은 콘솔 OEM 코드페이지로 바이트를 쓴다.
    /// 이를 UTF-8 로 해석하면 한글이 전부 깨진다("C 드라이브의 볼륨" → "C ����̺��� ����").
    ///
    /// <c>chcp 65001</c> 접두사로 해결되지 않는다 — 실측 결과 출력이 리다이렉트된 경우
    /// 코드페이지 전환이 내장 명령의 출력에 반영되지 않아 깨짐이 그대로였다(디코딩 실패 51자 동일).
    /// 고칠 지점은 생산자가 아니라 <b>소비자(디코딩)</b> 쪽이다.
    ///
    /// 한계: UTF-8 로 출력하는 프로그램(일부 최신 CLI)은 반대로 깨질 수 있다. 자동 판별 수단이 없어
    /// OS 기본값인 OEM 을 택했다 — 셸 내장 명령과 대다수 Windows 도구가 이쪽이다.
    /// </summary>
    private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

    private static Encoding ResolveConsoleEncoding()
    {
        try
        {
            // .NET Core 이상은 949 같은 레거시 코드페이지가 기본 제공되지 않는다 — 공급자 등록 필요.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            // 코드페이지를 얻지 못하면 UTF-8 로 폴백한다(영문 환경에서는 사실상 동일).
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }

    public async Task<ScriptResult> ExecutePowerShellAsync(string script, int timeoutMs = 30000, string? workingDirectory = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{Escape(script)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        return await ExecuteAsync(psi, timeoutMs, ct).ConfigureAwait(false);
    }

    public async Task<ScriptResult> ExecuteCmdAsync(string command, int timeoutMs = 30000, string? workingDirectory = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{Escape(command)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        return await ExecuteAsync(psi, timeoutMs, ct).ConfigureAwait(false);
    }

    private async Task<ScriptResult> ExecuteAsync(ProcessStartInfo psi, int timeoutMs, CancellationToken ct)
    {
        await _concurrencyLimit.WaitAsync(ct).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stdoutSb = new StringBuilder();
            var stderrSb = new StringBuilder();

            // 폭주 출력(무한 echo 등)이 메모리를 잠식하지 않도록 스트림당 상한. 초과분은 버린다(도구가 추가로 절단·고지).
            const int MaxStreamChars = 200_000;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null && stdoutSb.Length < MaxStreamChars) stdoutSb.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null && stderrSb.Length < MaxStreamChars) stderrSb.AppendLine(e.Data);
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ScriptResult
                {
                    ExitCode = -1,
                    Stderr = $"Failed to start process: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // timeout
                TryKill(process);
                stopwatch.Stop();
                return new ScriptResult
                {
                    ExitCode = -1,
                    TimedOut = true,
                    Stdout = stdoutSb.ToString(),
                    Stderr = "Execution timed out",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                };
            }
            catch (OperationCanceledException)
            {
                // 외부 ct 취소
                TryKill(process);
                stopwatch.Stop();
                return new ScriptResult
                {
                    ExitCode = -1,
                    Stdout = stdoutSb.ToString(),
                    Stderr = "Execution canceled",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                };
            }

            // stdout/stderr flush 대기
            try { process.WaitForExit(); } catch { /* ignore */ }

            stopwatch.Stop();
            return new ScriptResult
            {
                ExitCode = process.ExitCode,
                Stdout = stdoutSb.ToString(),
                Stderr = stderrSb.ToString(),
                DurationMs = stopwatch.ElapsedMilliseconds,
                TimedOut = false,
            };
        }
        finally
        {
            _concurrencyLimit.Release();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("ScriptExecutor", "Kill failed", ex);
        }
    }

    private static string Escape(string s)
        => s.Replace("\"", "\\\"");
}
