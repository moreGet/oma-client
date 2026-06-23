namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>IProgress 페이로드. UI 진행률 바인딩용.</summary>
public readonly record struct IntegrityProgress
{
    public int ProcessedFiles { get; init; }
    public int TotalFiles { get; init; }
    /// <summary>현재 처리 중 파일 상대경로(상태표시줄용).</summary>
    public string? CurrentFile { get; init; }
    /// <summary>0.0~1.0. TotalFiles==0이면 0.</summary>
    public double Fraction => TotalFiles <= 0 ? 0d : (double)ProcessedFiles / TotalFiles;
}
