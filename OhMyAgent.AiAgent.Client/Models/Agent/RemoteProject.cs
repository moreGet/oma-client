using System;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>서버측 프로젝트 표현. GET/POST /api/v1/projects. 직렬화: AgentJson.Options.</summary>
public sealed record RemoteProject(
    [property: JsonPropertyName("id")]          string Id,
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("updated_utc")] DateTimeOffset UpdatedUtc);

/// <summary>프로젝트 upsert 요청 본문. RemoteId 보유 시 갱신, 없으면 신규 생성.</summary>
public sealed record RemoteProjectUpsert(
    [property: JsonPropertyName("remote_id")] string? RemoteId,
    [property: JsonPropertyName("name")]      string Name);

/// <summary>대화(세션) push 본문. POST /api/v1/projects/{id}/conversations.</summary>
public sealed record RemoteConversation(
    [property: JsonPropertyName("id")]       string Id,
    [property: JsonPropertyName("title")]    string Title,
    [property: JsonPropertyName("messages")] System.Collections.Generic.IReadOnlyList<AgentMessage> Messages);
