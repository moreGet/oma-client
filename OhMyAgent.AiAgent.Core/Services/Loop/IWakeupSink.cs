using System;
using System.Diagnostics.CodeAnalysis;
using OhMyAgent.AiAgent.Client.Models.Loop;

namespace OhMyAgent.AiAgent.Client.Services.Loop;

/// <summary>
/// schedule_wakeup 도구 → 루프 엔진 사이의 단방향 우편함(ITodoService 와 같은 인메모리 공유 슬롯 패턴).
///
/// 도구가 루프 컨트롤러를 직접 알면 Core 안에 순환 의존이 생기고, 서브에이전트가 부모 루프를 조종할 길도 열린다.
/// 그래서 도구는 이 우편함만 알고, 루프가 턴 전후로 창을 열고 닫아 "이번 턴에 쓰인 값"만 정확히 1회 회수한다.
/// </summary>
public interface IWakeupSink
{
    /// <summary>루프 턴 시작 직전. 이전 턴의 잔여 값을 버리고 수신 창을 연다.</summary>
    void Arm(Guid loopId, LoopMode mode);

    /// <summary>수신 창을 닫는다(루프 종료). 잔여 값 폐기.</summary>
    void Disarm();

    /// <summary>도구가 호출. 수신 창이 닫혀 있으면 false — 루프 밖 호출은 부작용 없이 거부된다.</summary>
    bool TryWrite(WakeupRequest request, out string? rejectReason);

    /// <summary>루프가 턴 종료 후 1회 소비. 소비 후 내부 값은 비워진다.</summary>
    bool TryConsume([MaybeNullWhen(false)] out WakeupRequest request);

    /// <summary>수신 창이 열려 있는지(도구가 사용자 안내 문구를 분기할 때 쓴다).</summary>
    bool IsArmed { get; }

    /// <summary>현재 열려 있는 창의 루프 모드. 고정 간격이면 도구는 값을 쓰지 않는다.</summary>
    LoopMode ArmedMode { get; }
}
