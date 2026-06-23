using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 설치 디렉토리 바이너리 무결성 검사. SHA256 기반.
/// 매니페스트 영속: %APPDATA%\OhMyAgent.AiAgent.Client\integrity\&lt;key&gt;.manifest.json.
/// 매니페스트를 검사 대상 폴더 밖(사용자 프로필)에 두어 바이너리+매니페스트 동시 재생성 자기위조를 방지.
/// &lt;key&gt;는 대상 절대경로를 정규화한 뒤의 SHA256 해시에서 파생. 직렬화: AgentJson.Options.
/// </summary>
public interface IBinaryIntegrityService
{
    /// <summary>현재 앱 설치 디렉토리(AppDomain.CurrentDomain.BaseDirectory)를 반환.</summary>
    string GetDefaultTargetDirectory();

    /// <summary>
    /// 대상 디렉토리에 대한 매니페스트 경로를 반환.
    /// (%APPDATA%\OhMyAgent.AiAgent.Client\integrity\&lt;정규화경로 SHA256 파생 key&gt;.manifest.json)
    /// 부작용 없는 순수 경로 계산 — 저장 디렉토리 생성은 저장 시점에 보장된다.
    /// </summary>
    string GetManifestPath(string targetDirectory);

    /// <summary>매니페스트 존재 여부.</summary>
    bool ManifestExists(string targetDirectory);

    /// <summary>매니페스트 로드. 없거나 손상 시 null.</summary>
    Task<IntegrityManifest?> LoadManifestAsync(
        string targetDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// 대상 디렉토리를 스캔해 새 매니페스트를 생성하고 디스크에 원자적 저장(tmp→Move).
    /// 진행률은 파일별로 보고. 반환값은 baseline-only 결과(모든 파일 Ok로 표기, IsBaselineOnly=true).
    /// </summary>
    Task<IntegrityScanResult> GenerateBaselineAsync(
        IntegrityScanOptions options,
        IProgress<IntegrityProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 대상 디렉토리를 매니페스트와 비교 검증.
    /// manifest가 null이면 GetManifestPath에서 로드 시도; 그래도 없으면 AgentException.
    /// 파일별 해싱→비교→분류, 진행률 보고, 취소 지원.
    /// </summary>
    Task<IntegrityScanResult> VerifyAsync(
        IntegrityScanOptions options,
        IntegrityManifest? manifest = null,
        IProgress<IntegrityProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 단일 파일의 SHA256(대문자 hex)을 스트리밍 계산. 읽기 실패 시 AgentException.
    /// (테스트/재계산용 보조 API)
    /// </summary>
    Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken ct = default);
}
