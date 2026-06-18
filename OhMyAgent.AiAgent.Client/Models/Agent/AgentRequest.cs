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
    [property: JsonPropertyName("metadata")]   RequestMetadata Metadata);
