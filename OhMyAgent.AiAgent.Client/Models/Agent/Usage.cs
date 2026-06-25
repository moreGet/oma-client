using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>토큰 사용량 (서버 API-SPEC §usage). 서버 필드명 정렬.</summary>
public sealed record Usage(
    [property: JsonPropertyName("prompt_tokens")]     int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")]      int TotalTokens = 0);
