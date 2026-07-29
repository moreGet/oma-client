using System;
using System.Diagnostics.CodeAnalysis;
using OhMyAgent.AiAgent.Client.Models.Loop;

namespace OhMyAgent.AiAgent.Client.Services.Loop;

/// <summary>
/// IWakeupSink 기본 구현 — 락으로 보호되는 단일 슬롯. 도구는 도구 실행 스레드에서,
/// 루프는 자신의 백그라운드 Task 에서 접근하므로 스레드 안전이 필수다.
/// </summary>
public sealed class WakeupSink : IWakeupSink
{
    private readonly object _lock = new();
    private bool _armed;
    private LoopMode _mode = LoopMode.FixedInterval;
    private WakeupRequest? _pending;

    public bool IsArmed
    {
        get { lock (_lock) return _armed; }
    }

    public LoopMode ArmedMode
    {
        get { lock (_lock) return _mode; }
    }

    public void Arm(Guid loopId, LoopMode mode)
    {
        // loopId 는 계약에만 남긴다 — 동시 루프 1개(A1)라 지금은 식별에 쓸 일이 없고,
        // 저장해 두면 "쓰이지 않는 상태"가 되어 다중 루프 지원 시 잘못된 필터의 씨앗이 된다.
        _ = loopId;
        lock (_lock)
        {
            _armed = true;
            _mode = mode;
            // 이전 턴에 쓰였지만 회수되지 않은 값은 버린다 — 지난 턴의 페이싱 결정이 이번 턴에 되살아나면 안 된다.
            _pending = null;
        }
    }

    public void Disarm()
    {
        lock (_lock)
        {
            _armed = false;
            _pending = null;
        }
    }

    public bool TryWrite(WakeupRequest request, out string? rejectReason)
    {
        if (request is null)
        {
            rejectReason = "예약 정보가 비어 있습니다.";
            return false;
        }

        lock (_lock)
        {
            if (!_armed)
            {
                rejectReason = "현재 /loop 실행 중이 아닙니다.";
                return false;
            }

            // 한 턴에 여러 번 부르면 마지막 값이 이긴다 — 모델이 생각을 바꿔 다시 부르는 편이 정상이다.
            _pending = request;
            rejectReason = null;
            return true;
        }
    }

    public bool TryConsume([MaybeNullWhen(false)] out WakeupRequest request)
    {
        lock (_lock)
        {
            if (_pending is null)
            {
                request = null!;
                return false;
            }

            request = _pending;
            _pending = null;
            return true;
        }
    }
}
