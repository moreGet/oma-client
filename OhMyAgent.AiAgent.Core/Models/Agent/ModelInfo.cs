using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>사용 가능한 모델 정보 (서버 API-SPEC §models). 서버 필드명 정렬.</summary>
public sealed record ModelInfo(
    [property: JsonPropertyName("id")]            string Id,
    [property: JsonPropertyName("name")]          string Name,
    [property: JsonPropertyName("provider_type")] string ProviderType,
    [property: JsonPropertyName("active")]        bool Active);
