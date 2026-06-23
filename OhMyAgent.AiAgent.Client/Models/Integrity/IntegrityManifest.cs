using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 무결성 기준 매니페스트 전체. integrity.manifest.json으로 영속.
/// 직렬화: AgentJson.Options(System.Text.Json).
/// </summary>
public sealed record IntegrityManifest
{
    /// <summary>스키마 버전(향후 마이그레이션 대비). 현재 1.</summary>
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    /// <summary>매니페스트 생성 UTC 시각.</summary>
    [JsonPropertyName("created_utc")]    public DateTimeOffset CreatedUtc { get; init; }
    /// <summary>생성 시점 대상 디렉토리 식별 라벨(절대경로 표시는 지양, 검증용 보조).</summary>
    [JsonPropertyName("root_label")]     public string? RootLabel { get; init; }
    /// <summary>해시 알고리즘 식별자. 현재 "SHA256".</summary>
    [JsonPropertyName("algorithm")]      public string Algorithm { get; init; } = "SHA256";
    /// <summary>파일별 기대 해시 목록.</summary>
    [JsonPropertyName("entries")]        public IReadOnlyList<IntegrityManifestEntry> Entries { get; init; } = [];
}
