using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>컴포저에 첨부된 로컬 파일 1건(클라이언트 메타).</summary>
public sealed record Attachment
{
    [JsonPropertyName("file_path")]    public required string FilePath { get; init; }   // 로컬 절대경로 (전송 시 제외 가능)
    [JsonPropertyName("file_name")]    public required string FileName { get; init; }
    [JsonPropertyName("size_bytes")]   public long SizeBytes { get; init; }
    [JsonPropertyName("content_type")] public string? ContentType { get; init; }         // MIME 추정, 미지정 가능
}
