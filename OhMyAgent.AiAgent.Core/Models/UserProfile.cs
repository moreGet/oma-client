using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>GET /api/v1/users/me 응답. 서버 보유 프로필(읽기 전용 표시용).</summary>
public sealed record UserProfile(
    [property: JsonPropertyName("username")]     string Username,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("organization")] string? Organization,
    [property: JsonPropertyName("email")]        string? Email);
