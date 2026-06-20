using System;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>최근 사용한 작업 디렉토리 1건. AppSettings.RecentWorkspaces에 영속.</summary>
/// <remarks>
/// AppSettings는 Newtonsoft로 직렬화되나 이 record는 그 하위로 함께 직렬화된다.
/// PascalCase 프로퍼티(Newtonsoft 기본) + STJ 어트리뷰트(STJ 사용 시)로 양립하도록 둔다.
/// settings.json에는 PascalCase(Path/DisplayName/LastUsedUtc)로 기록된다.
/// </remarks>
public sealed record WorkspaceHistoryEntry
{
    [JsonPropertyName("path")]          public required string Path { get; init; }
    [JsonPropertyName("display_name")]  public required string DisplayName { get; init; }
    [JsonPropertyName("last_used_utc")] public DateTimeOffset LastUsedUtc { get; init; }
}
