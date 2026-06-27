using System;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 서버측 프로젝트 표현. GET/POST /api/v1/projects (서버 API-SPEC §프로젝트/대화 동기화).
/// 직렬화: AgentJson.Options. 서버는 client_id↔id 매핑을 관리하므로 ClientId 도 노출(미응답 시 null).
/// </summary>
public sealed record RemoteProject(
    [property: JsonPropertyName("id")]                 string Id,
    [property: JsonPropertyName("name")]               string Name,
    [property: JsonPropertyName("updated_utc")]        DateTimeOffset UpdatedUtc,
    [property: JsonPropertyName("client_id")]          string? ClientId = null,
    [property: JsonPropertyName("created_utc")]        DateTimeOffset? CreatedUtc = null,
    [property: JsonPropertyName("conversation_count")] int ConversationCount = 0);

/// <summary>
/// 프로젝트 생성/업서트 요청 본문 (서버 §POST /projects: {client_id, name}).
/// client_id = 클라 GUID(안정 키). 재전송 시 서버가 동일 id 를 반환한다.
/// </summary>
public sealed record RemoteProjectUpsert(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("name")]      string Name);

/// <summary>
/// 대화(세션) 업서트 본문. POST /api/v1/projects/{id}/conversations
/// (서버 §: {client_id, title, created_utc, updated_utc, messages[]}). client_id = 클라 GUID.
/// </summary>
public sealed record RemoteConversation(
    [property: JsonPropertyName("client_id")]   string ClientId,
    [property: JsonPropertyName("title")]       string Title,
    [property: JsonPropertyName("created_utc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("updated_utc")] DateTimeOffset UpdatedUtc,
    [property: JsonPropertyName("messages")]    System.Collections.Generic.IReadOnlyList<AgentMessage> Messages);
