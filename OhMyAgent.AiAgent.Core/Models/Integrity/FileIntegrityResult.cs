namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>파일 1건의 검증 결과(매니페스트 기대값 + 디스크 실제값 + 분류).</summary>
public sealed record FileIntegrityResult
{
    public required string RelativePath { get; init; }
    public required IntegrityStatus Status { get; init; }
    /// <summary>매니페스트의 기대 해시. Unexpected면 null.</summary>
    public string? ExpectedSha256 { get; init; }
    /// <summary>디스크 실제 해시. Missing/Corrupted면 null.</summary>
    public string? ActualSha256 { get; init; }
    /// <summary>디스크 실제 크기(없으면 null).</summary>
    public long? ActualSize { get; init; }
    /// <summary>(선택) 서명 상태.</summary>
    public SignatureStatus Signature { get; init; } = SignatureStatus.NotChecked;
    /// <summary>오류/부가 설명(예: 파일 잠김, 접근 거부).</summary>
    public string? Detail { get; init; }
}
