using System.Text.Json;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// SSE 스트림 이벤트. IAgentApiClient 가 와이어 이벤트를 파싱해 방출한다.
/// (API_CONTRACT §4.2 / §6)
/// </summary>
public abstract record AgentStreamEvent;

/// <summary>event: message_start — 응답 시작.</summary>
public sealed record MessageStart(string Id, string Model) : AgentStreamEvent;

/// <summary>event: content_delta — 텍스트 토큰 스트림.</summary>
public sealed record ContentDelta(string Text) : AgentStreamEvent;

/// <summary>event: tool_call — 모델이 도구 실행을 요청.</summary>
public sealed record ToolCallEvent(string Id, string Name, JsonElement Args) : AgentStreamEvent;

/// <summary>event: message_stop — 응답 종료. StopReason 은 raw 문자열로 유지.</summary>
public sealed record MessageStop(string StopReason, Usage Usage) : AgentStreamEvent;

/// <summary>event: error — 서버 오류.</summary>
public sealed record ErrorEvent(string Code, string Message) : AgentStreamEvent;
