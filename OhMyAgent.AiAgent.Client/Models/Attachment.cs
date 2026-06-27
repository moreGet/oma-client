using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>컴포저에 첨부된 로컬 파일 1건(클라이언트 메타).</summary>
public sealed record Attachment
{
    // 로컬 절대경로 — UI 전용, 서버 미전송. [JsonIgnore]+required 조합은 STJ가 거부하므로 required 제거.
    [JsonIgnore]                       public string FilePath { get; init; } = string.Empty;
    [JsonPropertyName("file_name")]    public required string FileName { get; init; }
    [JsonPropertyName("content_type")] public string? ContentType { get; init; }         // MIME 추정, 미지정 가능
    [JsonPropertyName("size_bytes")]   public long SizeBytes { get; init; }

    /// <summary>전송 시 base64 인코딩된 파일 바이트(스펙 data_base64). UI 단계에선 null → 직렬화 생략.</summary>
    [JsonPropertyName("data_base64")]  public string? DataBase64 { get; init; }
}
