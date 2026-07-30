using System;

namespace OhMyAgent.AiAgent.Client.Models.Loop;

/// <summary>
/// 루프 엔진이 소비자(GUI 상단바 / 헤드리스 stderr)로 방출하는 이벤트.
/// AgentEvent 와 같은 폐쇄형 계층 관례를 따른다 — 소비처가 switch 로 전부 훑을 수 있어야
/// 새 이벤트를 추가했을 때 표시 누락이 눈에 띈다.
/// </summary>
public abstract record LoopEvent;

/// <summary>루프가 시작됨(첫 턴 직전).</summary>
public sealed record LoopStarted(LoopStatusSnapshot Status) : LoopEvent;

/// <summary>턴 1회 시작.</summary>
public sealed record LoopTurnStarting(int Iteration, int MaxIterations) : LoopEvent;

/// <summary>턴 1회 종료(성공/실패).</summary>
public sealed record LoopTurnFinished(int Iteration, bool Succeeded, string? ErrorMessage) : LoopEvent;

/// <summary>다음 턴까지 대기 진입. <paramref name="Reason"/> 은 모델이 남긴 페이싱 사유(자율 모드).</summary>
public sealed record LoopWaiting(DateTimeOffset NextRunAt, TimeSpan Delay, string? Reason) : LoopEvent;

/// <summary>대기 중 1초 주기 카운트다운. 소비처가 과하다면 스로틀할 책임을 진다(헤드리스는 10초 배수만 출력).</summary>
public sealed record LoopTick(TimeSpan Remaining, int Iteration, int MaxIterations) : LoopEvent;

/// <summary>사용자에게 보여줄 정보성 안내(오류 아님).</summary>
public sealed record LoopNotice(string Text) : LoopEvent;

/// <summary>루프 종료. 수명당 정확히 1회만 발화한다.</summary>
public sealed record LoopStopped(LoopStopReason Reason, int Iterations) : LoopEvent;
