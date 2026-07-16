using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 설치 디렉토리 바이너리 무결성 검사 — 실제 구현.
/// SHA256 스트리밍 해싱, 매니페스트 직렬화(AgentJson.Options), 비교 분류, 진행률/취소 지원.
/// 서명 검증기는 선택 주입(null이면 서명 검사 비활성).
///
/// 매니페스트는 검사 대상 폴더가 아닌 사용자 프로필 영역(%APPDATA%\OhMyAgent.AiAgent.Client\integrity)
/// 에 저장된다. 변조자가 바이너리와 매니페스트를 함께 재생성하는 자기위조(self-forgery)를 방지하기 위함.
/// 대상 디렉토리별로 파일이 구분되도록 정규화된 대상 절대경로의 SHA256 해시에서 파생한 키를 파일명에 사용.
/// </summary>
public sealed class BinaryIntegrityService : IBinaryIntegrityService
{
    /// <summary>
    /// 매니페스트 파일명 접미사(하위호환 상수). 실제 파일명은 &lt;key&gt;.manifest.json 형태로 키가 접두된다.
    /// </summary>
    public const string ManifestFileName = "integrity.manifest.json";

    /// <summary>%APPDATA% 하위 매니페스트 저장 루트의 앱 폴더명.</summary>
    private const string AppDataFolderName = "OhMyAgent.AiAgent.Client";

    /// <summary>앱 폴더 하위 매니페스트 전용 서브디렉토리명.</summary>
    private const string ManifestSubFolderName = "integrity";

    /// <summary>파일명 키로 사용할 대상경로 해시 hex의 길이(앞 N자).</summary>
    private const int KeyHashLength = 32;

    /// <summary>가독성 보조용 디렉토리명 접두 최대 길이.</summary>
    private const int LabelPrefixMaxLength = 24;

    private const int StreamBufferSize = 81920; // 80KB. 스트리밍 해싱 버퍼.

    /// <summary>현재 서명 알고리즘 식별자.</summary>
    private const string SignatureAlgorithmId = "HMACSHA256";

    /// <summary>현재 서명 키 버전(키 로테이션 대비). 키 파생 솔트와 함께 사용.</summary>
    private const int CurrentSignatureKeyVersion = 1;

    // HMAC 서명 키 베이스(앱 내장 비밀) — 여러 조각으로 분산해 단순 문자열 스캔 추출을 어렵게 함.
    // 한계: 동일 권한 공격자가 리버스로 값·파생로직 복원 시 위조 가능. HMAC은 단독 변조 탐지만 제공.
    private static readonly byte[] SecretA = [0x4F, 0x68, 0x4D, 0x79, 0x41, 0x67, 0x65, 0x6E, 0x74];
    private static readonly byte[] SecretB = [0x49, 0x6E, 0x74, 0x65, 0x67, 0x72, 0x69, 0x74, 0x79];
    private static readonly byte[] SecretC = [0xA7, 0x3C, 0x91, 0x5E, 0x02, 0xD4, 0x88, 0x1B, 0x6F, 0xC0, 0x29, 0x7A];

    private readonly IAuthenticodeVerifier? _authenticode;

    public BinaryIntegrityService(IAuthenticodeVerifier? authenticode = null)
    {
        _authenticode = authenticode;
    }

    /// <inheritdoc />
    public string GetDefaultTargetDirectory() =>
        AppDomain.CurrentDomain.BaseDirectory;

    /// <inheritdoc />
    public string GetManifestPath(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        // 부작용 없는 순수 경로 계산. 디렉토리 생성은 저장 시점(SaveManifestAsync)에서 보장.
        var root = GetManifestStorageRoot();
        var fileName = $"{DeriveManifestKey(targetDirectory)}.manifest.json";
        return Path.Combine(root, fileName);
    }

    /// <summary>매니페스트 저장 루트(%APPDATA%\OhMyAgent.AiAgent.Client\integrity)를 반환.</summary>
    private static string GetManifestStorageRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, AppDataFolderName, ManifestSubFolderName);
    }

    /// <summary>
    /// 대상 디렉토리 절대경로로부터 안정적인 고유 파일명 키를 파생.
    /// 경로 정규화(GetFullPath, 후행 구분자 제거, 소문자화) → SHA256 → 앞 N자 hex.
    /// 가독성 보조로 디렉토리명 일부를 안전 문자만 남겨 접두.
    /// </summary>
    private static string DeriveManifestKey(string targetDirectory)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(targetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            // 정규화 실패 시 원문 사용(키 안정성보다 견고성 우선).
            normalized = targetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // OrdinalIgnoreCase 비교 일관성 위해 소문자화.
        normalized = normalized.ToLowerInvariant();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var key = hex.Length > KeyHashLength ? hex[..KeyHashLength] : hex;

        var label = SanitizeLabel(TryGetRootLabel(targetDirectory));
        return string.IsNullOrEmpty(label) ? key : $"{label}_{key}";
    }

    /// <summary>디렉토리명을 파일명에 안전한 문자(영숫자/대시/언더스코어)만 남겨 접두용으로 정제.</summary>
    private static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var sb = new StringBuilder(label.Length);
        foreach (var ch in label)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                sb.Append(ch);
            if (sb.Length >= LabelPrefixMaxLength)
                break;
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <inheritdoc />
    public bool ManifestExists(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return false;
        return File.Exists(GetManifestPath(targetDirectory));
    }

    /// <summary>매니페스트 로드 결과 분류(서명 검증 결과 구분용 내부 표현).</summary>
    private enum ManifestLoadStatus
    {
        /// <summary>파일이 존재하지 않음.</summary>
        Absent,
        /// <summary>파일 읽기/역직렬화 실패(손상).</summary>
        Corrupted,
        /// <summary>로드는 성공했으나 HMAC 서명 부재/불일치(변조 의심).</summary>
        SignatureFailed,
        /// <summary>로드 및 서명 검증 모두 성공.</summary>
        Valid
    }

    /// <inheritdoc />
    public async Task<IntegrityManifest?> LoadManifestAsync(
        string targetDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var (status, manifest) = await LoadManifestCoreAsync(targetDirectory, ct).ConfigureAwait(false);

        // 기존 계약 유지: 없음/손상/서명실패 모두 null 반환(재생성 유도). 서명실패는 손상으로 간주.
        return status == ManifestLoadStatus.Valid ? manifest : null;
    }

    /// <summary>
    /// 매니페스트를 로드하고 HMAC 서명까지 검증해 상태를 분류한다.
    /// 부재/손상/서명실패/정상을 구분 가능한 형태로 반환(공개 계약과 무관한 내부 헬퍼).
    /// </summary>
    private async Task<(ManifestLoadStatus Status, IntegrityManifest? Manifest)> LoadManifestCoreAsync(
        string targetDirectory,
        CancellationToken ct)
    {
        var path = GetManifestPath(targetDirectory);

        return await Task.Run(() =>
        {
            if (!File.Exists(path))
                return (ManifestLoadStatus.Absent, (IntegrityManifest?)null);

            IntegrityManifest? manifest;
            try
            {
                var json = File.ReadAllText(path);
                manifest = JsonSerializer.Deserialize<IntegrityManifest>(json, AgentJson.Options);
            }
            catch (Exception ex)
            {
                // 손상 매니페스트는 건너뛰고 손상으로 분류(재생성 유도).
                AppLog.Warn("BinaryIntegrityService", $"LoadManifestAsync failed for '{path}'", ex);
                return (ManifestLoadStatus.Corrupted, (IntegrityManifest?)null);
            }

            if (manifest is null)
                return (ManifestLoadStatus.Corrupted, (IntegrityManifest?)null);

            if (!VerifyManifestSignature(manifest))
            {
                // 서명 부재(구버전) 또는 불일치 = 변조 의심. 손상과 구분되는 분류로 반환.
                AppLog.Warn("BinaryIntegrityService", $"manifest signature verification failed for '{path}'.");
                return (ManifestLoadStatus.SignatureFailed, manifest);
            }

            return (ManifestLoadStatus.Valid, manifest);
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IntegrityScanResult> GenerateBaselineAsync(
        IntegrityScanOptions options,
        IProgress<IntegrityProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = options.TargetDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new AgentException($"대상 디렉토리 없음: {root}");

        var manifestPath = GetManifestPath(root);
        var files = EnumerateTargets(root, options, manifestPath);
        var total = files.Count;

        var results = new List<FileIntegrityResult>(total);
        var entries = new List<IntegrityManifestEntry>(total);

        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var full = files[i];
            var rel  = ToRelativePath(root, full);

            try
            {
                var size = new FileInfo(full).Length;
                var hash = await ComputeSha256CoreAsync(full, ct).ConfigureAwait(false);

                entries.Add(new IntegrityManifestEntry
                {
                    RelativePath = rel,
                    Sha256       = hash,
                    Size         = size
                });
                results.Add(new FileIntegrityResult
                {
                    RelativePath   = rel,
                    Status         = IntegrityStatus.Ok,
                    ExpectedSha256 = hash,
                    ActualSha256   = hash,
                    ActualSize     = size,
                    Signature      = SignatureStatus.NotChecked
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // baseline 중 개별 파일 오류 → Corrupted로 흡수, 매니페스트엔 미포함.
                results.Add(new FileIntegrityResult
                {
                    RelativePath = rel,
                    Status       = IntegrityStatus.Corrupted,
                    Detail       = DescribeError(ex)
                });
            }

            progress?.Report(new IntegrityProgress
            {
                ProcessedFiles = i + 1,
                TotalFiles     = total,
                CurrentFile    = rel
            });
        }

        ct.ThrowIfCancellationRequested();

        var manifest = new IntegrityManifest
        {
            SchemaVersion = 1,
            CreatedUtc    = DateTimeOffset.UtcNow,
            RootLabel     = TryGetRootLabel(root),
            Algorithm     = "SHA256",
            // Entries는 결정적 직렬화를 위해 RelativePath 정렬 적용(서명 대상과 동일 순서).
            Entries       = SortEntries(entries)
        };

        // HMAC-SHA256 서명 부여: canonical 바이트(서명 필드 null)에 대해 계산 후 채워 저장.
        var signature = ComputeManifestSignature(manifest, CurrentSignatureKeyVersion);
        manifest = manifest with
        {
            Signature           = signature,
            SignatureAlgorithm  = SignatureAlgorithmId,
            SignatureKeyVersion = CurrentSignatureKeyVersion
        };

        await SaveManifestAsync(manifestPath, manifest, ct).ConfigureAwait(false);

        return BuildResult(results, root, isBaselineOnly: true);
    }

    /// <inheritdoc />
    public async Task<IntegrityScanResult> VerifyAsync(
        IntegrityScanOptions options,
        IntegrityManifest? manifest = null,
        IProgress<IntegrityProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = options.TargetDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new AgentException($"대상 디렉토리 없음: {root}");

        if (manifest is null)
        {
            // 직접 로드+서명검증 수행: 부재와 서명실패를 구분해 서명실패가 "매니페스트 없음"으로 묻히지 않게 한다.
            var (status, loaded) = await LoadManifestCoreAsync(root, ct).ConfigureAwait(false);
            switch (status)
            {
                case ManifestLoadStatus.Valid:
                    manifest = loaded ?? throw new AgentException("매니페스트 로드 실패 — '기준 생성'을 먼저 실행하세요.");
                    break;
                case ManifestLoadStatus.SignatureFailed:
                    throw new AgentException("매니페스트 서명 검증 실패 — 변조 가능성. '기준 생성'으로 재생성하세요.");
                case ManifestLoadStatus.Corrupted:
                case ManifestLoadStatus.Absent:
                default:
                    throw new AgentException("매니페스트 없음 — '기준 생성'을 먼저 실행하세요.");
            }
        }

        var manifestPath = GetManifestPath(root);

        // 디스크 파일 집합 D: 상대경로(OrdinalIgnoreCase) → 실제 전체경로.
        var diskFiles = EnumerateTargets(root, options, manifestPath);
        var diskMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in diskFiles)
            diskMap[ToRelativePath(root, full)] = full;

        // 매니페스트 엔트리 집합 M: 상대경로(OrdinalIgnoreCase) → 엔트리.
        var manifestMap = new Dictionary<string, IntegrityManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
            manifestMap[entry.RelativePath] = entry;

        // 진행 총량 = 매니페스트 엔트리 + Unexpected(매니페스트에 없는 디스크 파일).
        var unexpected = diskMap.Keys.Where(k => !manifestMap.ContainsKey(k)).ToList();
        var total = manifestMap.Count + unexpected.Count;

        var results = new List<FileIntegrityResult>(total);
        var processed = 0;

        // 1) 매니페스트 엔트리 검증.
        foreach (var (rel, entry) in manifestMap)
        {
            ct.ThrowIfCancellationRequested();

            if (!diskMap.TryGetValue(rel, out var full))
            {
                results.Add(new FileIntegrityResult
                {
                    RelativePath   = rel,
                    Status         = IntegrityStatus.Missing,
                    ExpectedSha256 = entry.Sha256,
                    Detail         = "파일 없음"
                });
            }
            else
            {
                results.Add(await VerifyEntryAsync(full, rel, entry, options.VerifySignatures, ct)
                    .ConfigureAwait(false));
            }

            progress?.Report(new IntegrityProgress
            {
                ProcessedFiles = ++processed,
                TotalFiles     = total,
                CurrentFile    = rel
            });
        }

        // 2) Unexpected: 디스크에 있으나 매니페스트에 없음.
        foreach (var rel in unexpected)
        {
            ct.ThrowIfCancellationRequested();
            var full = diskMap[rel];
            long? size = null;
            try { size = new FileInfo(full).Length; }
            catch (Exception ex) { AppLog.Warn("BinaryIntegrityService", $"size read failed '{full}'", ex); }

            results.Add(new FileIntegrityResult
            {
                RelativePath = rel,
                Status       = IntegrityStatus.Unexpected,
                ActualSize   = size,
                Signature    = options.VerifySignatures ? VerifySignature(full) : SignatureStatus.NotChecked,
                Detail       = "매니페스트에 없는 파일"
            });

            progress?.Report(new IntegrityProgress
            {
                ProcessedFiles = ++processed,
                TotalFiles     = total,
                CurrentFile    = rel
            });
        }

        return BuildResult(results, root, isBaselineOnly: false);
    }

    /// <inheritdoc />
    public async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            return await ComputeSha256CoreAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentException($"해시 계산 실패: {filePath}", ex);
        }
    }

    // --- 내부 헬퍼 ---------------------------------------------------------

    private async Task<FileIntegrityResult> VerifyEntryAsync(
        string full,
        string rel,
        IntegrityManifestEntry entry,
        bool verifySignatures,
        CancellationToken ct)
    {
        try
        {
            var size = new FileInfo(full).Length;
            var hash = await ComputeSha256CoreAsync(full, ct).ConfigureAwait(false);
            var match = string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase);
            var signature = verifySignatures ? VerifySignature(full) : SignatureStatus.NotChecked;

            return new FileIntegrityResult
            {
                RelativePath   = rel,
                Status         = match ? IntegrityStatus.Ok : IntegrityStatus.Modified,
                ExpectedSha256 = entry.Sha256,
                ActualSha256   = hash,
                ActualSize     = size,
                Signature      = signature,
                Detail         = match ? null : "해시 불일치"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 개별 파일 읽기 실패 → Corrupted로 흡수(스캔 중단 금지).
            return new FileIntegrityResult
            {
                RelativePath   = rel,
                Status         = IntegrityStatus.Corrupted,
                ExpectedSha256 = entry.Sha256,
                Detail         = DescribeError(ex)
            };
        }
    }

    private SignatureStatus VerifySignature(string full)
    {
        if (_authenticode is null)
            return SignatureStatus.NotChecked;
        try
        {
            return _authenticode.Verify(full);
        }
        catch (Exception ex)
        {
            AppLog.Warn("BinaryIntegrityService", $"signature verify failed '{full}'", ex);
            return SignatureStatus.Invalid;
        }
    }

    private static async Task<string> ComputeSha256CoreAsync(string filePath, CancellationToken ct)
    {
        // 자기 자신 exe / 로드된 dll 도 읽도록 Read|Delete 공유, 비동기 스트림.
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            StreamBufferSize,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash); // 대문자 hex.
    }

    private static List<string> EnumerateTargets(
        string root,
        IntegrityScanOptions options,
        string manifestPath)
    {
        var searchOption = options.Recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        // 확장자 비교용 집합(소문자, 점 포함). 빈 목록이면 모든 파일.
        var extensions = options.IncludeExtensions is { Count: > 0 }
            ? new HashSet<string>(options.IncludeExtensions, StringComparer.OrdinalIgnoreCase)
            : null;

        // 매니페스트는 %APPDATA% 하위(대상 폴더 밖) 저장이라 스캔 대상에 포함될 일이 없음.
        // ExcludeManifestFile 자기제외는 불필요하나 극단적 경우 안전망으로 옵션 존중해 유지.
        var manifestFull = SafeGetFullPath(manifestPath);
        var results = new List<string>();

        // 접근 거부 하위 디렉토리는 스킵하기 위해 옵션 지정 열거.
        IEnumerable<string> enumerated;
        try
        {
            enumerated = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = options.Recursive,
                IgnoreInaccessible    = true,
                AttributesToSkip      = FileAttributes.ReparsePoint // 심볼릭 링크/정션 추적 안 함.
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("BinaryIntegrityService", $"enumerate failed '{root}'", ex);
            return results;
        }

        foreach (var full in enumerated)
        {
            // 매니페스트 파일 자신 제외(안전망 — 통상은 대상 폴더 밖이라 해당 없음).
            if (options.ExcludeManifestFile && manifestFull is not null &&
                string.Equals(SafeGetFullPath(full), manifestFull, StringComparison.OrdinalIgnoreCase))
                continue;

            if (extensions is not null)
            {
                var ext = Path.GetExtension(full);
                if (!extensions.Contains(ext))
                    continue;
            }

            results.Add(full);
        }

        return results;
    }

    private static string ToRelativePath(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    private static string? SafeGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("BinaryIntegrityService", $"GetFullPath failed '{path}'", ex);
            return null;
        }
    }

    private static string? TryGetRootLabel(string root)
    {
        try
        {
            return new DirectoryInfo(root).Name;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "접근 거부",
        IOException                 => "파일 잠김/읽기 실패",
        _                           => ex.Message
    };

    // --- HMAC 서명 ---------------------------------------------------------

    /// <summary>
    /// 매니페스트의 HMAC-SHA256 서명(Base64)을 계산한다.
    /// 서명 대상(canonical)은 Signature/SignatureAlgorithm/SignatureKeyVersion 필드를 null로 둔 사본을
    /// AgentJson.Options로 직렬화한 UTF-8 바이트(서명 필드는 WhenWritingNull로 출력에서 생략됨).
    /// Entries는 RelativePath OrdinalIgnoreCase 정렬로 결정성 보장.
    /// </summary>
    private static string ComputeManifestSignature(IntegrityManifest manifest, int keyVersion)
    {
        var canonicalManifest = manifest with
        {
            Entries            = SortEntries(manifest.Entries),
            Signature          = null,
            SignatureAlgorithm = null,
            SignatureKeyVersion = null
        };

        var json  = JsonSerializer.Serialize(canonicalManifest, AgentJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        var key = GetSigningKey(keyVersion);
        var mac = HMACSHA256.HashData(key, bytes);
        return Convert.ToBase64String(mac);
    }

    /// <summary>
    /// Entries를 RelativePath OrdinalIgnoreCase 기준으로 정렬한 새 목록 반환.
    /// 저장·검증 양쪽에서 동일 적용해 직렬화 결정성을 보장.
    /// </summary>
    private static IReadOnlyList<IntegrityManifestEntry> SortEntries(IReadOnlyList<IntegrityManifestEntry> entries)
    {
        var sorted = entries.ToList();
        sorted.Sort(static (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return sorted;
    }

    /// <summary>
    /// 서명 키를 파생한다. 내장 비밀(SecretA/B/C) + 키버전 솔트 + 머신/유저 식별자를
    /// 결합해 HMACSHA256으로 파생하여, 매니페스트만으로 재서명을 어렵게 한다.
    /// 머신/유저 식별자가 환경에서 안정적으로 얻어지지 않으면 내장 비밀만으로 폴백
    /// (1차 목표인 파일 단독 변조 탐지는 유지). DPAPI 등 추가 의존은 도입하지 않음.
    /// </summary>
    private static byte[] GetSigningKey(int keyVersion)
    {
        // 내장 비밀 결합(베이스 키 재료).
        var baseMaterial = new byte[SecretA.Length + SecretB.Length + SecretC.Length + 4];
        var offset = 0;
        Buffer.BlockCopy(SecretA, 0, baseMaterial, offset, SecretA.Length); offset += SecretA.Length;
        Buffer.BlockCopy(SecretB, 0, baseMaterial, offset, SecretB.Length); offset += SecretB.Length;
        Buffer.BlockCopy(SecretC, 0, baseMaterial, offset, SecretC.Length); offset += SecretC.Length;
        // 키버전을 솔트로 혼입.
        BitConverter.GetBytes(keyVersion).CopyTo(baseMaterial, offset);

        // 머신/유저 식별자 — 매니페스트만 옮겨선 재서명 어렵게. 실패 시 빈 식별자(폴백).
        var identity = SafeMachineUserIdentity();

        // 내장 비밀을 키로, 식별자를 데이터로 HMAC → 머신/유저 바인딩된 파생 키.
        return HMACSHA256.HashData(baseMaterial, Encoding.UTF8.GetBytes(identity));
    }

    /// <summary>머신명+유저명 기반의 안정적 식별 문자열. 얻기 실패 시 빈 문자열.</summary>
    private static string SafeMachineUserIdentity()
    {
        try
        {
            var machine = Environment.MachineName ?? string.Empty;
            var user    = Environment.UserName ?? string.Empty;
            return $"{machine}{user}".ToLowerInvariant();
        }
        catch (Exception ex)
        {
            AppLog.Warn("BinaryIntegrityService", "machine/user identity unavailable", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// 매니페스트 서명을 재계산해 상수시간 비교로 검증.
    /// 서명 부재(구버전) 또는 불일치 시 false(=변조 의심).
    /// </summary>
    private static bool VerifyManifestSignature(IntegrityManifest manifest)
    {
        if (string.IsNullOrEmpty(manifest.Signature))
            return false; // 서명 부재 = 하위호환 비대상, 검증 실패로 간주(재생성 유도).

        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            return false; // 손상된 서명 형식 = 변조 의심.
        }

        var keyVersion = manifest.SignatureKeyVersion ?? CurrentSignatureKeyVersion;
        var expected   = Convert.FromBase64String(ComputeManifestSignature(manifest, keyVersion));

        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private async Task SaveManifestAsync(string path, IntegrityManifest manifest, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var tmp  = path + ".tmp";
                var json = JsonSerializer.Serialize(manifest, AgentJson.Options);
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true); // 부분 쓰기 방지: 원자적 교체.
            }
            catch (Exception ex)
            {
                AppLog.Warn("BinaryIntegrityService", $"SaveManifestAsync failed '{path}'", ex);
                throw new AgentException($"매니페스트 저장 실패: {path}", ex);
            }
        }, ct).ConfigureAwait(false);
    }

    private static IntegrityScanResult BuildResult(
        List<FileIntegrityResult> results,
        string targetDirectory,
        bool isBaselineOnly)
    {
        var ok         = 0;
        var modified   = 0;
        var corrupted  = 0;
        var missing    = 0;
        var unexpected = 0;

        foreach (var r in results)
        {
            switch (r.Status)
            {
                case IntegrityStatus.Ok:         ok++; break;
                case IntegrityStatus.Modified:   modified++; break;
                case IntegrityStatus.Corrupted:  corrupted++; break;
                case IntegrityStatus.Missing:    missing++; break;
                case IntegrityStatus.Unexpected: unexpected++; break;
            }
        }

        return new IntegrityScanResult
        {
            Files           = results,
            ScannedUtc      = DateTimeOffset.UtcNow,
            TargetDirectory = targetDirectory,
            IsBaselineOnly  = isBaselineOnly,
            OkCount         = ok,
            ModifiedCount   = modified,
            CorruptedCount  = corrupted,
            MissingCount    = missing,
            UnexpectedCount = unexpected
        };
    }
}
