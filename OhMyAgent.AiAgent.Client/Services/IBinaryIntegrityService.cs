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
///
/// 매니페스트 자체의 변조 탐지를 위해 HMAC-SHA256 서명을 부여한다. 서명 대상은 서명 관련 필드를
/// 제외한 매니페스트의 정규(canonical) 직렬화 바이트(Entries는 RelativePath OrdinalIgnoreCase 정렬).
/// 서명 키는 앱 내장 비밀을 머신/유저 식별자와 결합해 파생하므로, 매니페스트 파일 단독 변조에 대한
/// tamper-evidence를 제공한다(동일 권한 공격자가 바이너리를 리버스해 키를 추출하면 위조 가능 — 한계).
/// 서명 부재(구버전) 또는 불일치 매니페스트는 검증 실패로 간주하며 '기준 생성'으로 재생성해야 한다.
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

    /// <summary>
    /// 매니페스트 로드. 없거나 손상, 또는 HMAC 서명 부재/불일치(변조 의심) 시 null.
    /// (서명 실패도 손상으로 간주해 null 반환 — 재생성 유도.)
    /// </summary>
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
    /// manifest가 null이면 GetManifestPath에서 로드 시도; 부재/손상이면 "매니페스트 없음" AgentException,
    /// HMAC 서명 검증 실패(부재 포함)면 "매니페스트 서명 검증 실패 — 변조 가능성" AgentException(부재와 구분).
    /// 호출자가 manifest를 직접 전달한 경우 서명 검증은 수행하지 않는다(이미 신뢰된 객체로 간주).
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
