using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

// 에이전트 레지스트리 REST 계약(§공유 계약 SYNC — Go 서버와 100% 동일해야 함) 와이어 DTO.
// 직렬화 정책: AgentJson.Options 는 Web(camelCase) 기본이라 프로퍼티 네이밍 정책 의존은 계약을 깬다
// (agent_id → agentId). 따라서 RemoteSession 관례대로 전 필드 [JsonPropertyName]으로 snake_case 를
// 명시 고정한다. 시각은 RFC3339 → DateTimeOffset(created_at/updated_at 관례).

/// <summary>POST /api/v1/agents/register 요청 본문.</summary>
public sealed record RegisterRequest(
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("tags")]         IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("model")]        string? Model,
    [property: JsonPropertyName("version")]      string? Version);

/// <summary>POST /api/v1/agents/register 응답. 루프 주기·리스 TTL 은 서버 권장값.</summary>
public sealed record RegisterResponse(
    [property: JsonPropertyName("agent_id")]                   string AgentId,
    [property: JsonPropertyName("lease_ttl_seconds")]         int LeaseTtlSeconds,
    [property: JsonPropertyName("heartbeat_interval_seconds")] int HeartbeatIntervalSeconds);

/// <summary>POST /api/v1/agents/{id}/heartbeat 응답(200).</summary>
public sealed record HeartbeatResponse(
    [property: JsonPropertyName("status")]            string Status,
    [property: JsonPropertyName("lease_ttl_seconds")] int LeaseTtlSeconds);

/// <summary>Agent 레코드(발견/단건 응답 항목). status 는 서버 계산값 → 문자열로 수신(미지 값 내성).</summary>
public sealed record AgentDescriptor(
    [property: JsonPropertyName("agent_id")]          string AgentId,
    [property: JsonPropertyName("name")]              string Name,
    [property: JsonPropertyName("endpoint_url")]      string EndpointUrl,
    [property: JsonPropertyName("capabilities")]      IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("tags")]              IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("model")]             string? Model,
    [property: JsonPropertyName("status")]            string Status,
    [property: JsonPropertyName("last_heartbeat_at")] DateTimeOffset? LastHeartbeatAt);

/// <summary>GET /api/v1/agents 응답 envelope: {agents:[...]}.</summary>
public sealed record DiscoverResponse(
    [property: JsonPropertyName("agents")] IReadOnlyList<AgentDescriptor> Agents);

/// <summary>POST /api/v1/agents/{id}/token 응답 — 대상별 단명 A2A 토큰(브로커).</summary>
public sealed record A2aToken(
    [property: JsonPropertyName("token")]              string Token,
    [property: JsonPropertyName("expires_in_seconds")] int ExpiresInSeconds,
    [property: JsonPropertyName("audience_agent_id")]  string AudienceAgentId);

/// <summary>GET /api/v1/agents/a2a-public-key 응답 — 수신측 서명 검증용 공개키.</summary>
public sealed record A2aPublicKey(
    [property: JsonPropertyName("kid")]            string Kid,
    [property: JsonPropertyName("alg")]            string Alg,
    [property: JsonPropertyName("public_key_pem")] string PublicKeyPem);

/// <summary>
/// GET /api/v1/agents 발견 필터. 직렬화 대상이 아니라 쿼리스트링 빌더 입력이다
/// (?capability=&amp;tag=&amp;status=&amp;q=&amp;exclude_self=). null 필드는 쿼리에서 생략된다.
/// </summary>
public sealed record DiscoverQuery(
    string? Capability = null,
    string? Tag = null,
    string? Status = null,
    string? Query = null,
    string? ExcludeSelf = null);
