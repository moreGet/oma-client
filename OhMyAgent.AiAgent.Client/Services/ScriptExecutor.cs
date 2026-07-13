using System;
using System.Diagnostics;
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
            Debug.WriteLine($"[ScriptExecutor] Kill failed: {ex.Message}");
        }
    }

    private static string Escape(string s)
        => s.Replace("\"", "\\\"");
}
