using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// System.Diagnostics.Process 기반 셸 실행기.
/// Windows: PowerShell / CMD (바이트 불변). Linux/macOS: 모든 타입 → /bin/bash -c.
/// 동시 실행 프로세스 수를 SemaphoreSlim 으로 제한.
///
/// <paramref name="activities"/> 는 선택적 관찰자다(null 이면 종전과 완전히 동일하게 동작한다).
/// 여기가 자식 PID 를 아는 유일한 지점이라 태스크 매니저의 "강제 종료"가 성립하려면 이곳에서 등록해야 한다 —
/// 관리 스레드는 강제 종료할 수 없으므로 실제로 멈출 수 있는 대상은 이 자식 프로세스뿐이다.
/// </summary>
public class ScriptExecutor(IAgentActivityRegistry? activities = null) : IScriptExecutor
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
    private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding(OperatingSystem.IsWindows());

    /// <summary>
    /// 콘솔 출력 디코딩 인코딩을 OS 별로 해석한다. 테스트가 <paramref name="isWindows"/> 를 명시 주입해
    /// 양쪽 OS 분기를 프로세스 스폰 없이 검증한다.
    /// </summary>
    internal static Encoding ResolveConsoleEncoding(bool isWindows)
    {
        // Linux/macOS 콘솔은 UTF-8 — OEM 코드페이지 조회 자체를 건너뛴다(리눅스에서 예외/오판 리스크 제거).
        if (!isWindows)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

    /// <summary>
    /// 셸 호출 사양. Windows 는 <see cref="ArgLine"/>(문자열 인자, 바이트 불변)로,
    /// Linux/macOS 는 <see cref="ArgList"/>(argv 직접 전달 → 재파싱·이스케이프 회피)로 채운다.
    /// </summary>
    internal readonly record struct ShellInvocation(string FileName, IReadOnlyList<string> ArgList, string? ArgLine);

    /// <summary>
    /// OS 라우팅 순수 메서드. 테스트가 <paramref name="isWindows"/> 를 명시 주입해 양쪽 분기를 검증한다.
    /// Windows: 기존 로직 1:1(Arguments 문자열, Escape 동일) — 바이트 불변.
    /// Linux/macOS: 모든 <see cref="ScriptType"/> → /bin/bash -c(ArgumentList 방식).
    /// </summary>
    internal static ShellInvocation ResolveShell(ScriptType type, string script, bool isWindows)
    {
        if (isWindows)
        {
            // ── Windows: 기존 로직 1:1(Arguments 문자열, Escape 동일) — 바이트 불변 ──
            return type == ScriptType.Cmd
                ? new ShellInvocation("cmd.exe", Array.Empty<string>(),
                      $"/c \"{Escape(script)}\"")
                : new ShellInvocation("powershell.exe", Array.Empty<string>(),
                      $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{Escape(script)}\"");
        }

        // ── Linux/macOS: 모든 ScriptType → /bin/bash. ArgumentList 로 넘겨 셸 재파싱/이스케이프 회피 ──
        //    (Arguments 문자열 대신 argv 직접 구성 → 따옴표·$·백틱 이스케이프 불필요, 인젝션 표면↓)
        return new ShellInvocation("/bin/bash", new[] { "-c", script }, null);
    }

    private static ProcessStartInfo BuildStartInfo(ShellInvocation inv, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = inv.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };
        if (inv.ArgLine is not null)
            psi.Arguments = inv.ArgLine;                  // Windows 경로(불변)
        else
            foreach (var a in inv.ArgList) psi.ArgumentList.Add(a);   // Linux 경로(argv 직접)

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        return psi;
    }

    public async Task<ScriptResult> ExecutePowerShellAsync(string script, int timeoutMs = 30000, string? workingDirectory = null, CancellationToken ct = default)
    {
        var inv = ResolveShell(ScriptType.PowerShell, script, OperatingSystem.IsWindows());
        return await ExecuteAsync(BuildStartInfo(inv, workingDirectory), timeoutMs, script, ct).ConfigureAwait(false);
    }

    public async Task<ScriptResult> ExecuteCmdAsync(string command, int timeoutMs = 30000, string? workingDirectory = null, CancellationToken ct = default)
    {
        var inv = ResolveShell(ScriptType.Cmd, command, OperatingSystem.IsWindows());
        return await ExecuteAsync(BuildStartInfo(inv, workingDirectory), timeoutMs, command, ct).ConfigureAwait(false);
    }

    /// <summary><paramref name="commandText"/> 는 태스크 매니저 표시용 원문이다(실행에는 쓰이지 않는다).</summary>
    private async Task<ScriptResult> ExecuteAsync(
        ProcessStartInfo psi, int timeoutMs, string commandText, CancellationToken ct)
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

            // 자식 프로세스 추적 — 이 블록을 어떤 경로로 벗어나도(정상 종료·타임아웃·취소·예외)
            // using 이 해제를 보장한다. 해제가 빠지면 살아 있는 프로세스가 "고아"로 오판된다.
            using var tracked = Track(process, commandText);

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

    /// <summary>
    /// 방금 띄운 셸 프로세스를 태스크 매니저에 등록한다. 관찰 전용이므로 <b>어떤 실패도 실행에 영향을 주지 않는다</b>
    /// (계측이 실행을 깨뜨리면 안 된다). PID 단독은 신원이 되지 않아 시작 시각·이름을 함께 기록한다 — PID 는 재사용된다.
    /// </summary>
    private IDisposable? Track(Process process, string commandText)
    {
        if (activities is null) return null;

        try
        {
            DateTimeOffset? startedAt = null;
            try { startedAt = new DateTimeOffset(process.StartTime).ToUniversalTime(); }
            catch (Exception) { /* 시작 시각을 못 읽으면 강제 종료 대상에서 제외된다(엄한 쪽) */ }

            return activities.TrackChildProcess(
                new TrackedProcessIdentity(process.Id, process.ProcessName, startedAt),
                commandText);
        }
        catch (Exception ex)
        {
            AppLog.Warn("ScriptExecutor", $"자식 프로세스 추적 등록 실패: {ex.Message}");
            return null;
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
