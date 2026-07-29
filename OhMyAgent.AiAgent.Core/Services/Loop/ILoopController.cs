using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Loop;

namespace OhMyAgent.AiAgent.Client.Services.Loop;

/// <summary>
/// 루프 1회분 턴을 실제로 실행하는 껍데기 — GUI/헤드리스가 각자 주입한다.
/// 이 델리게이트 덕분에 루프 엔진은 IAgentOrchestrator 를 몰라도 되고, Core 가 WPF·콘솔 어느 쪽에도 기울지 않는다.
/// 구현은 예외를 삼키고 <see cref="LoopTurnOutcome.Fail"/> 로 환원할 책임을 진다.
/// </summary>
public delegate Task<LoopTurnOutcome> LoopTurnRunner(LoopTurnContext ctx, CancellationToken ct);

/// <summary>
/// 같은 프롬프트를 반복 실행하는 루프 엔진. 프로세스당 동시 루프는 1개다(A1) —
/// 단일 AgentSession 을 공유하므로 두 루프가 겹치면 대화 이력이 오염된다.
/// </summary>
public interface ILoopController : IDisposable
{
    bool IsRunning { get; }

    LoopStatusSnapshot Status { get; }

    /// <summary>백그라운드 스레드에서 발화 — 구독자가 UI 마샬 책임(ITodoService 관례와 동일).</summary>
    event EventHandler<LoopEvent>? LoopChanged;

    /// <summary>비차단 시작. 이미 실행 중이거나 프롬프트가 비면 false + 사용자용 안내를 돌려준다.</summary>
    bool TryStart(LoopStartRequest request, LoopTurnRunner runner, CancellationToken externalCt, out string? error);

    /// <summary>멱등. 실행 중이 아니면 무시한다.</summary>
    void Stop(LoopStopReason reason);

    /// <summary>종료 경로에서 루프 Task 가 실제로 접힐 때까지 대기(기본 2초).</summary>
    Task StopAndWaitAsync(LoopStopReason reason, TimeSpan? timeout = null);
}
