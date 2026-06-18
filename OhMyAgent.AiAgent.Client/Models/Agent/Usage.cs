using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>토큰 사용량 (API_CONTRACT §4.2 usage).</summary>
public sealed record Usage(
    [property: JsonPropertyName("input_tokens")]  int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);
