using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>POST /api/v1/agent/chat 요청 본문 (API_CONTRACT §4.1).</summary>
public sealed record AgentRequest(
    [property: JsonPropertyName("model")]      string Model,
    [property: JsonPropertyName("stream")]     bool Stream,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")]   IReadOnlyList<AgentMessage> Messages,
    [property: JsonPropertyName("tools")]      IReadOnlyList<ToolSchema> Tools,
    [property: JsonPropertyName("metadata")]   RequestMetadata Metadata,
    [property: JsonPropertyName("thinking"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ThinkingRequest? Thinking = null);

/// <summary>
/// 확장 사고 요청 설정. null 이면 직렬화 생략 → 서버는 사고 미사용(기본).
/// Type: "adaptive"(최신 모델) | "enabled"(BudgetTokens 필요, 구형 사고 모델).
/// </summary>
public sealed record ThinkingRequest(
    [property: JsonPropertyName("type")]         string Type,
    [property: JsonPropertyName("budget_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int BudgetTokens = 0);
