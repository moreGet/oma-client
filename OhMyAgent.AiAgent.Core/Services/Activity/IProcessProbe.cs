using System;
using System.Diagnostics;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// OS 프로세스 조회·종료의 유일한 접점. 왜 인터페이스로 떼는가 —
/// 이 두 동작만 격리하면 레지스트리 전체를 <b>실제 프로세스를 하나도 죽이지 않고</b> 테스트할 수 있다.
/// (판정 로직은 <see cref="ProcessIdentityRules"/> 에, 실제 kill 은 이 경계 뒤에만 있다.)
/// </summary>
public interface IProcessProbe
{
    /// <summary>지금 상태를 관측한다. 해당 PID 가 없으면 null(= 종료됨).</summary>
    ProcessObservation? Observe(int pid);

    /// <summary>프로세스 트리를 종료한다. 성공 여부와 실패 사유를 돌려준다(예외를 던지지 않는다).</summary>
    bool TryKill(int pid, out string? error);
}

/// <summary><see cref="Process"/> 기반 기본 구현. 조회 실패는 예외가 아니라 값으로 환원한다.</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public ProcessObservation? Observe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);

            // 이미 끝난 핸들을 잡았을 수도 있다 — 그 경우 "없음"과 같게 취급한다.
            if (p.HasExited) return null;

            DateTimeOffset? started = null;
            try
            {
                // 권한이 부족하면(다른 계정/승격 프로세스) 여기서 던진다. 그때는 시작 시각 없음으로 남기고,
                // 동일성 확인 불가 → 강제 종료 대상에서 제외된다(엄한 쪽으로 기운다).
                started = new DateTimeOffset(p.StartTime).ToUniversalTime();
            }
            catch (Exception)
            {
                started = null;
            }

            return new ProcessObservation(p.Id, SafeName(p), started);
        }
        catch (ArgumentException)
        {
            // GetProcessById: 해당 PID 의 프로세스가 없다.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Activity", $"프로세스 관측 실패(pid {pid}): {ex.Message}");
            return null;
        }
    }

    public bool TryKill(int pid, out string? error)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited)
            {
                error = null;
                return true;
            }

            // 자식까지 함께 접는다 — 셸이 띄운 손자 프로세스가 남으면 "정리"가 아니다(ScriptExecutor 와 동일 정책).
            p.Kill(entireProcessTree: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; }
        catch (Exception) { return string.Empty; }
    }
}
