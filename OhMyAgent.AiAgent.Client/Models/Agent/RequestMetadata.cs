using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>요청 메타데이터 (서버 API-SPEC §metadata).</summary>
public sealed record RequestMetadata(
    [property: JsonPropertyName("os")]             string Os,
    [property: JsonPropertyName("workspace_root")] string WorkspaceRoot,
    [property: JsonPropertyName("client_version")] string ClientVersion);
