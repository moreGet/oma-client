using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 서버가 알리는 클라이언트 버전 정책. GET /api/v1/client/version.
/// 미구현 서버(404/오프라인)는 graceful 하게 null 로 처리한다. 직렬화: AgentJson.Options(snake_case).
/// </summary>
public sealed record ClientVersionInfo(
    [property: JsonPropertyName("latest")]            string Latest,
    [property: JsonPropertyName("minimum_supported")] string MinimumSupported,
    [property: JsonPropertyName("download_url")]      string? DownloadUrl,
    [property: JsonPropertyName("notice")]            string? Notice,
    [property: JsonPropertyName("mandatory")]         bool Mandatory);
