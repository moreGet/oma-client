using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 서버측 대화 세션 요약. GET /api/v1/agent/sessions 의 항목.
/// 직렬화: AgentJson.Options. 시각은 ISO-8601 UTC.
/// </summary>
public sealed record RemoteSessionSummary(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("title")]      string Title,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

/// <summary>GET /api/v1/agent/sessions 응답 envelope: {sessions:[...]}.</summary>
public sealed record RemoteSessionList(
    [property: JsonPropertyName("sessions")] IReadOnlyList<RemoteSessionSummary> Sessions);

/// <summary>
/// 서버측 대화 세션 전체. GET /api/v1/agent/sessions/{id}.
/// <see cref="Data"/> 는 클라이언트 히스토리(직렬화된 ChatSessionRecord) — 서버에는 불투명.
/// </summary>
public sealed record RemoteSession(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("title")]      string Title,
    [property: JsonPropertyName("data")]       JsonElement Data,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);
