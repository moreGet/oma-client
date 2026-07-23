using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>스캔 입력 옵션. 기본값 = 설치 디렉토리 + 표준 바이너리 확장자.</summary>
public sealed record IntegrityScanOptions
{
    /// <summary>검사 대상 루트. 기본 AppDomain.CurrentDomain.BaseDirectory.</summary>
    public required string TargetDirectory { get; init; }
    /// <summary>대상 확장자(소문자, 점 포함). 기본 [".exe", ".dll"]. 빈 목록이면 모든 파일.</summary>
    public IReadOnlyList<string> IncludeExtensions { get; init; } = [".exe", ".dll"];
    /// <summary>하위 디렉토리 재귀 포함 여부. 기본 true.</summary>
    public bool Recursive { get; init; } = true;
    /// <summary>Authenticode 서명 검사 수행 여부. 기본 false.</summary>
    public bool VerifySignatures { get; init; }
    /// <summary>매니페스트 자기 자신 파일은 검사에서 제외(항상 true 권장).</summary>
    public bool ExcludeManifestFile { get; init; } = true;
}
