using System.Text.Json;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// IAgentOrchestrator.RunAsync 가 ViewModel 로 방출하는 UI-facing 이벤트.
/// 와이어 이벤트보다 풍부하다.
/// </summary>
public abstract record AgentEvent;

/// <summary>assistant 프로즈 토큰.</summary>
public sealed record AgentTextDelta(string Text) : AgentEvent;

/// <summary>하나의 assistant 턴이 닫힘.</summary>
public sealed record AgentAssistantMessageComplete(string Text) : AgentEvent;

/// <summary>도구 호출 시작.</summary>
public sealed record AgentToolCallStarted(string CallId, string ToolName, JsonElement Args, ToolRisk Risk) : AgentEvent;

/// <summary>승인 대기 (Manual/AutoSafe 게이트).</summary>
public sealed record AgentAwaitingApproval(string CallId, string ToolName, JsonElement Args, ToolRisk Risk) : AgentEvent;

/// <summary>도구 호출 결과.</summary>
public sealed record AgentToolCallResult(string CallId, string ToolName, ToolResult Result) : AgentEvent;

/// <summary>루프 반복 진행.</summary>
public sealed record AgentIterationAdvanced(int Iteration, int MaxIterations) : AgentEvent;

/// <summary>루프 완료 (최종 응답).</summary>
public sealed record AgentDone(string FinalText, Usage? LastUsage) : AgentEvent;

/// <summary>오류 (서버/취소/최대 반복 등).</summary>
public sealed record AgentError(string Code, string Message) : AgentEvent;

/// <summary>
/// 정보성 알림 — 오류가 아니다. 전사에만 남고 오류 배너/재시도 버튼을 띄우지 않는다.
///
/// AgentError 로 보내면 안 되는 이유: VM 의 분류기가 모르는 코드를 전부 Other 로 보고
/// "오류" 배너 + "다시 시도" 를 띄운다. 컨텍스트 압축이나 응답 잘림은 정상적인 살림살이라
/// 사용자에게 알릴 가치는 있지만 실패가 아니며, "다시 시도" 는 엉뚱한 안내다.
/// </summary>
public sealed record AgentNotice(string Text) : AgentEvent;
