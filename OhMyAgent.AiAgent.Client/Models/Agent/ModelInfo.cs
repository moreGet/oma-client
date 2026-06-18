using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>사용 가능한 모델 정보 (API_CONTRACT §3 models).</summary>
public sealed record ModelInfo(
    [property: JsonPropertyName("id")]              string Id,
    [property: JsonPropertyName("display_name")]    string DisplayName,
    [property: JsonPropertyName("supports_tools")]  bool SupportsTools,
    [property: JsonPropertyName("supports_vision")] bool SupportsVision);
