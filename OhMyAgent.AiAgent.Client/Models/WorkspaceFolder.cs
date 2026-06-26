using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 다중 루트 워크스페이스의 한 폴더 항목. 설정 파일에 PascalCase로 영속된다
/// (SettingsService.PersistenceOptions). 접근 토글(Enabled)로 활성/비활성 제어.
/// </summary>
public sealed record WorkspaceFolder
{
    [JsonPropertyName("Path")]    public required string Path { get; init; }
    [JsonPropertyName("Enabled")] public bool Enabled { get; init; } = true;
}
