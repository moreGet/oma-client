using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>서버가 추가로 차단할 명령/경로 패턴 1건. 클라 내장 디폴트에 더해진다(서버는 추가만, 제거 불가).</summary>
public sealed record CommandPattern(
    [property: JsonPropertyName("type")]        string? Type,        // "regex" | "substring" (기본 substring)
    [property: JsonPropertyName("pattern")]     string Pattern,
    [property: JsonPropertyName("reason")]      string? Reason,
    [property: JsonPropertyName("script_type")] string? ScriptType); // "any" | "powershell" | "cmd" (기본 any)

/// <summary>GET /api/v1/security/command-policy 응답. 둘 다 null/없음이면 디폴트만 적용.</summary>
public sealed record CommandSecurityPolicyResponse(
    [property: JsonPropertyName("blocked_patterns")] IReadOnlyList<CommandPattern>? BlockedPatterns,
    [property: JsonPropertyName("blocked_paths")]    IReadOnlyList<CommandPattern>? BlockedPaths);
