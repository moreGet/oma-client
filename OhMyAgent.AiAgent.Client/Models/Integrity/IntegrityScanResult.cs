using System;
using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>스캔 전체 결과 + 요약 카운트.</summary>
public sealed record IntegrityScanResult
{
    public required IReadOnlyList<FileIntegrityResult> Files { get; init; }
    public required DateTimeOffset ScannedUtc { get; init; }
    public required string TargetDirectory { get; init; }
    /// <summary>매니페스트 없이 baseline 생성만 했는지 여부(true면 비교 무의미).</summary>
    public bool IsBaselineOnly { get; init; }

    public int OkCount         { get; init; }
    public int ModifiedCount   { get; init; }
    public int CorruptedCount  { get; init; }
    public int MissingCount    { get; init; }
    public int UnexpectedCount { get; init; }

    /// <summary>모든 매니페스트 파일이 Ok이고 Unexpected가 없으면 true.</summary>
    public bool IsIntact =>
        ModifiedCount == 0 && CorruptedCount == 0 && MissingCount == 0 && UnexpectedCount == 0;
}
