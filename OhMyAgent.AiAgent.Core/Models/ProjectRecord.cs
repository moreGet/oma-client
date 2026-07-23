using System;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 디스크에 영속되는 프로젝트(대화 컨테이너) 1건. projects/{Id}.json.
/// 직렬화: AgentJson.Options(snake_case). 로컬 우선 + 선택적 서버 동기화.
/// </summary>
public sealed record ProjectRecord
{
    [JsonPropertyName("id")]          public required string Id { get; init; }
    [JsonPropertyName("name")]        public required string Name { get; init; }
    [JsonPropertyName("created_utc")] public DateTimeOffset CreatedUtc { get; init; }
    [JsonPropertyName("updated_utc")] public DateTimeOffset UpdatedUtc { get; init; }
    [JsonPropertyName("synced")]      public bool Synced { get; init; }
    [JsonPropertyName("remote_id")]   public string? RemoteId { get; init; }
}
