using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>매니페스트 1행: 디렉토리 루트 기준 상대경로의 기대 해시.</summary>
public sealed record IntegrityManifestEntry
{
    /// <summary>매니페스트 루트 기준 상대경로(항상 '/' 구분, 소문자 비교용 원본 보존).</summary>
    [JsonPropertyName("relative_path")] public required string RelativePath { get; init; }
    /// <summary>대문자 16진수 SHA256(64자).</summary>
    [JsonPropertyName("sha256")]        public required string Sha256 { get; init; }
    /// <summary>바이트 단위 파일 크기(빠른 사전 비교/표시용).</summary>
    [JsonPropertyName("size")]          public long Size { get; init; }
}
