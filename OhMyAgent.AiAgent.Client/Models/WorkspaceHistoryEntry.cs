using System;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>최근 사용한 작업 디렉토리 1건. AppSettings.RecentWorkspaces에 영속.</summary>
/// <remarks>
/// AppSettings는 영속용 System.Text.Json(SettingsJson.Options, PascalCase)로 직렬화되며
/// 이 record도 그 하위로 함께 직렬화된다. 디스크의 settings.json은 기존 Newtonsoft
/// 직렬화와 동일하게 PascalCase(Path/DisplayName/LastUsedUtc)로 기록·로드되어야 하므로
/// JsonPropertyName 으로 PascalCase 를 명시해 직렬화기 기본 정책과 무관하게 호환을 보장한다.
/// </remarks>
public sealed record WorkspaceHistoryEntry
{
    [JsonPropertyName("Path")]        public required string Path { get; init; }
    [JsonPropertyName("DisplayName")] public required string DisplayName { get; init; }
    [JsonPropertyName("LastUsedUtc")] public DateTimeOffset LastUsedUtc { get; init; }
}
