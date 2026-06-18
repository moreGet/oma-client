using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>요청 메타데이터 (API_CONTRACT §4.1 metadata).</summary>
public sealed record RequestMetadata(
    [property: JsonPropertyName("os")]             string Os,
    [property: JsonPropertyName("workspace")]      string Workspace,
    [property: JsonPropertyName("client_version")] string ClientVersion);
