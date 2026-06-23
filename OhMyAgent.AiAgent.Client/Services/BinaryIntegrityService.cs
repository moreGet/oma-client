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

    /// <inheritdoc />
    public async Task<IntegrityManifest?> LoadManifestAsync(
        string targetDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var path = GetManifestPath(targetDirectory);

        return await Task.Run(() =>
        {
            if (!File.Exists(path))
                return null;
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<IntegrityManifest>(json, AgentJson.Options);
            }
            catch (Exception ex)
            {
                // 손상 매니페스트는 건너뛰고 null 반환(재생성 유도).
                Debug.WriteLine($"[BinaryIntegrityService] LoadManifestAsync failed for '{path}': {ex.Message}");
                return null;
            }
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
            Entries       = entries
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

        manifest ??= await LoadManifestAsync(root, ct).ConfigureAwait(false);
        if (manifest is null)
            throw new AgentException("매니페스트 없음 — '기준 생성'을 먼저 실행하세요.");

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
            catch (Exception ex) { Debug.WriteLine($"[BinaryIntegrityService] size read failed '{full}': {ex.Message}"); }

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
            Debug.WriteLine($"[BinaryIntegrityService] signature verify failed '{full}': {ex.Message}");
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

        // 매니페스트는 이제 %APPDATA% 하위(대상 폴더 밖)에 저장되므로 스캔 대상에 포함될 일이 없다.
        // ExcludeManifestFile 자기제외 로직은 더 이상 필요치 않으나, 대상이 우연히 %APPDATA% 하위인
        // 극단적 경우의 안전망으로 옵션을 존중해 남겨 둔다.
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
            Debug.WriteLine($"[BinaryIntegrityService] enumerate failed '{root}': {ex.Message}");
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
            Debug.WriteLine($"[BinaryIntegrityService] GetFullPath failed '{path}': {ex.Message}");
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
                Debug.WriteLine($"[BinaryIntegrityService] SaveManifestAsync failed '{path}': {ex.Message}");
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
