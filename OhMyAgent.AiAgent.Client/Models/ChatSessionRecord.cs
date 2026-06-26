using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>디스크에 영속되는 한 대화 세션 전체. sessions/{Id}.json. 직렬화: AgentJson.Options.</summary>
public sealed record ChatSessionRecord
{
    [JsonPropertyName("id")]             public required string Id { get; init; }
    [JsonPropertyName("title")]          public required string Title { get; init; }
    [JsonPropertyName("created_utc")]    public DateTimeOffset CreatedUtc { get; init; }
    [JsonPropertyName("updated_utc")]    public DateTimeOffset UpdatedUtc { get; init; }
    [JsonPropertyName("workspace_root")] public string? WorkspaceRoot { get; init; }
    [JsonPropertyName("project_id")]     public string? ProjectId { get; init; }   // null = 미분류
    [JsonPropertyName("messages")]       public IReadOnlyList<AgentMessage> Messages { get; init; } = [];
}
