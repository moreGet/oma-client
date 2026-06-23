# 01. Architect Spec — 설치 디렉토리 바이너리 무결성 검사 (Binary Integrity Verification)

> 작성자: Architect 에이전트
> 대상 프로젝트: OhMyAgent.AiAgent.Client (net10.0-windows, WPF, MVVM)
> 산출물 범위: **설계 명세만**. .cs / .xaml 구현 파일은 후속 엔지니어 에이전트가 작성한다.

---

## 0. 코드베이스 현황 / 컨벤션 확인 (설계 전제)

설계는 아래 실제 코드베이스 관찰 결과에 맞춘다.

- **DI 컨테이너 없음.** `App.xaml.cs > OnStartup`에서 인터페이스+구현을 수동 인스턴스화한다.
  서비스 등록 지점은 기존 `var chatHistory = new ChatHistoryService();` 인근(아래 §7 참조).
- **인터페이스(IFoo) + sealed 구현(Foo) 패턴.** 예: `IChatHistoryService` / `ChatHistoryService`.
- **JSON 직렬화는 `System.Text.Json` + `AgentJson.Options`가 사실상 표준.**
  `ChatHistoryService`가 record를 `JsonSerializer.Serialize(record, AgentJson.Options)`로 저장/로드한다.
  (`Newtonsoft.Json`은 `AppSettings`(settings.json) 직렬화에만 쓰인다.)
  → **본 기능의 매니페스트는 `System.Text.Json` + `AgentJson.Options`로 통일한다.** record + `[JsonPropertyName]` 스타일을 따른다.
- **모델은 `sealed record` + `required` + `[JsonPropertyName(snake_case)]`.** (예: `ChatSessionRecord`, `WorkspaceHistoryEntry`.)
- **서비스 구현 관용구**: 무거운 IO는 `await Task.Run(() => { ... }, ct)`, `ConfigureAwait(false)`,
  파일 손상은 `Debug.WriteLine`으로 스킵, 치명적 실패는 `throw new AgentException(...)`, 원자적 쓰기는 `tmp → File.Move(overwrite:true)`.
- **ViewModel**: `partial class : ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`(소스 생성기), 생성자 주입.
- **View(XAML)**: `WindowStyle=None` + `AllowsTransparency=True` + 커스텀 타이틀바, 공유 StaticResource 테마
  (`WindowBg`, `SurfaceBg`, `BorderBrush`, `TextPrimary`, `TextSecondary`, `AccentGradient`, `AppFont`). `SettingsWindow`를 레퍼런스 삼는다.
- **취소/진행률**: 기존 서비스는 `CancellationToken ct = default`를 마지막 파라미터로 둔다. 본 기능은 추가로 `IProgress<T>`를 사용한다.

> **가정 1.** Authenticode 서명 검증은 "best-effort 선택 기능"으로 둔다. 1차 검증 축은 SHA256 해시이며, 서명 검증은 파일별 부가 메타로 노출하되 해시 불일치를 대체하지 않는다.
> **가정 2.** 매니페스트가 없을 때 "기준 생성(Baseline)" 모드로 현재 디렉토리 상태를 캡처해 매니페스트를 만든다(최초 1회). 이후 실행은 "검증(Verify)" 모드.
> **가정 3.** 메뉴/트레이/별도 윈도우 중 어디서 띄울지는 UIDesigner 재량이나, 본 스펙은 `SettingsWindow`와 동일한 독립 `IntegrityWindow`를 기본으로 한다.

---

## 1. 기능 개요 및 사용 시나리오

### 1.1 개요
현재 실행 중인 WPF 앱이 **자기 자신의 설치 디렉토리(`AppDomain.CurrentDomain.BaseDirectory`) 내부의 바이너리(.exe/.dll 등) 파일 무결성**을 SHA256 해시 기준으로 검사한다. 기준 매니페스트(파일별 기대 해시 목록)와 디스크 실제 상태를 비교해 각 파일을 **정상 / 변조 / 손상 / 누락 / 추가** 로 분류하고, 진행률·요약과 함께 UI로 표시한다.

### 1.2 핵심 흐름(상태 머신)
```
[Idle]
  │ ScanCommand (매니페스트 존재 → Verify / 없음 → 안내)
  ▼
[Hashing] ──(IProgress 진행률)──► [Comparing] ──► [Completed: 요약+파일별결과]
  │                                                  │
  └─(Cancel)──► [Cancelled]              [GenerateBaselineCommand]──► 매니페스트 저장 ──► [Completed]
```

### 1.3 사용 시나리오
1. **최초 기준 생성**: 매니페스트 없음 → 사용자가 "기준 매니페스트 생성" → 현재 디렉토리 바이너리 해시를 스냅샷 → `integrity.manifest.json` 저장.
2. **정기 무결성 검사**: 매니페스트 존재 → "검사 시작" → 진행률 바 표시 → 완료 후 파일별 상태 그리드 + 요약(정상 N / 변조 N / 손상 N / 누락 N / 추가 N).
3. **대상 디렉토리 변경**: 사용자가 `bin`/`obj` 등 빌드 산출물 경로를 골라 검사(기본값은 설치 위치).
4. **취소**: 대용량 디렉토리 해싱 중 사용자가 취소 → 즉시 중단(`OperationCanceledException` → Cancelled 상태).
5. **(선택) 서명 확인**: 각 파일의 Authenticode 서명 유효성을 부가 컬럼으로 확인.

---

## 2. 레이어 분해 (Models / Services / ViewModels / Views)

### Models — `OhMyAgent.AiAgent.Client.Models`
| 파일 | 타입 | 책임 |
|------|------|------|
| `Models/Integrity/IntegrityStatus.cs` | `enum` | 파일 단위 검증 결과 분류 |
| `Models/Integrity/SignatureStatus.cs` | `enum` | (선택) Authenticode 서명 상태 |
| `Models/Integrity/IntegrityManifestEntry.cs` | `sealed record` | 매니페스트 1행: 상대경로 + 기대 SHA256 + 크기 |
| `Models/Integrity/IntegrityManifest.cs` | `sealed record` | 매니페스트 전체(버전/생성시각/루트라벨/엔트리목록) — JSON 직렬화 대상 |
| `Models/Integrity/FileIntegrityResult.cs` | `sealed record` | 파일 1건 검증 결과(상태/기대해시/실제해시/크기/서명/메시지) |
| `Models/Integrity/IntegrityScanResult.cs` | `sealed record` | 스캔 전체 결과(파일목록 + 요약 카운트 + 매니페스트 메타) |
| `Models/Integrity/IntegrityProgress.cs` | `readonly record struct` | `IProgress<T>` 진행률 페이로드 |
| `Models/Integrity/IntegrityScanOptions.cs` | `sealed record` | 스캔 입력 옵션(대상디렉토리/필터/서명검사여부/재귀) |

### Services — `OhMyAgent.AiAgent.Client.Services`
| 파일 | 타입 | 책임 |
|------|------|------|
| `Services/IBinaryIntegrityService.cs` | `interface` | 해싱·매니페스트 입출력·검증 비교의 계약 |
| `Services/BinaryIntegrityService.cs` | `sealed class` | SHA256 스트리밍 해싱, 매니페스트 직렬화/로드, 비교 분류, 진행률/취소 |
| (선택) `Services/IAuthenticodeVerifier.cs` | `interface` | Authenticode 서명 검증 분리 계약 |
| (선택) `Services/AuthenticodeVerifier.cs` | `sealed class` | `X509Certificate.CreateFromSignedFile` + WinVerifyTrust 래핑 |

> 서명 검증을 별도 서비스로 뽑은 이유: WinVerifyTrust P/Invoke 의존성을 격리해 해싱 코어를 순수하게 유지하고, 비-Windows/미구현 환경에서 `null` 주입으로 무력화 가능.

### ViewModels — `OhMyAgent.AiAgent.Client.ViewModels`
| 파일 | 타입 | 책임 |
|------|------|------|
| `ViewModels/IntegrityViewModel.cs` | `partial class : ObservableObject` | 화면 상태/진행률/요약/커맨드, 서비스 호출·취소 토큰 관리 |
| `ViewModels/FileIntegrityItemViewModel.cs` | `partial class : ObservableObject` | 그리드 1행 표시 모델(상태색/아이콘/툴팁 등 표현 전용) |

### Views — `OhMyAgent.AiAgent.Client.Views`
| 파일 | 책임 | DataContext |
|------|------|-------------|
| `Views/IntegrityWindow.xaml` (+ `.xaml.cs`) | 독립 윈도우: 타이틀바/대상선택/진행률/결과 그리드/요약/액션버튼 | `IntegrityViewModel` |
| (선택) `Views/Converters.cs`에 변환기 추가 | `IntegrityStatus → Brush/아이콘` 변환기 | — |

> 컨버터는 신규 파일 대신 기존 `Views/Converters.cs`에 추가하거나, 컨벤션상 `Converters/` 네임스페이스 신설 가능. ViewModel에서 `FileIntegrityItemViewModel`이 표시 속성을 노출하면 컨버터 없이도 바인딩 가능(권장: 최소 컨버터).

---

## 3. 모델 정의 (구체 C# 시그니처)

### 3.1 enum

```csharp
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>파일 단위 무결성 검증 결과 분류.</summary>
public enum IntegrityStatus
{
    /// <summary>기대 해시와 실제 해시가 일치.</summary>
    Ok,
    /// <summary>매니페스트에 있고 파일도 있으나 해시 불일치(내용 변경됨).</summary>
    Modified,
    /// <summary>파일이 존재하나 읽기 실패/I/O 오류 등으로 해시 산출 불가(손상 의심).</summary>
    Corrupted,
    /// <summary>매니페스트에 있으나 디스크에 파일 없음.</summary>
    Missing,
    /// <summary>디스크에 있으나 매니페스트에 없음(예상치 못한 추가 파일).</summary>
    Unexpected
}
```

```csharp
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>(선택) Authenticode 서명 검증 상태. 해시 검증과 독립적인 부가 정보.</summary>
public enum SignatureStatus
{
    /// <summary>서명 검사를 하지 않음(옵션 꺼짐 또는 비대상 확장자).</summary>
    NotChecked,
    /// <summary>유효하게 서명되고 신뢰 체인 검증 통과.</summary>
    Valid,
    /// <summary>서명이 있으나 무효(체인 실패/만료/변조).</summary>
    Invalid,
    /// <summary>서명 없음(unsigned).</summary>
    Unsigned
}
```

### 3.2 record

```csharp
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>매니페스트 1행: 디렉토리 루트 기준 상대경로의 기대 해시.</summary>
public sealed record IntegrityManifestEntry
{
    /// <summary>매니페스트 루트 기준 상대경로(항상 '/' 구분, 소문자 비교용 원본 보존).</summary>
    [JsonPropertyName("relative_path")] public required string RelativePath { get; init; }
    /// <summary>대문자 16진수 SHA256(64자).</summary>
    [JsonPropertyName("sha256")]        public required string Sha256 { get; init; }
    /// <summary>바이트 단위 파일 크기(빠른 사전 비교/표시용).</summary>
    [JsonPropertyName("size")]          public long Size { get; init; }
}
```

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 무결성 기준 매니페스트 전체. integrity.manifest.json으로 영속.
/// 직렬화: AgentJson.Options(System.Text.Json).
/// </summary>
public sealed record IntegrityManifest
{
    /// <summary>스키마 버전(향후 마이그레이션 대비). 현재 1.</summary>
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    /// <summary>매니페스트 생성 UTC 시각.</summary>
    [JsonPropertyName("created_utc")]    public DateTimeOffset CreatedUtc { get; init; }
    /// <summary>생성 시점 대상 디렉토리 식별 라벨(절대경로 표시는 지양, 검증용 보조).</summary>
    [JsonPropertyName("root_label")]     public string? RootLabel { get; init; }
    /// <summary>해시 알고리즘 식별자. 현재 "SHA256".</summary>
    [JsonPropertyName("algorithm")]      public string Algorithm { get; init; } = "SHA256";
    /// <summary>파일별 기대 해시 목록.</summary>
    [JsonPropertyName("entries")]        public IReadOnlyList<IntegrityManifestEntry> Entries { get; init; } = [];
}
```

```csharp
using OhMyAgent.AiAgent.Client.Models;

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
```

```csharp
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
```

```csharp
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
```

```csharp
using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>스캔 입력 옵션. 기본값 = 설치 디렉토리 + 표준 바이너리 확장자.</summary>
public sealed record IntegrityScanOptions
{
    /// <summary>검사 대상 루트. 기본 AppDomain.CurrentDomain.BaseDirectory.</summary>
    public required string TargetDirectory { get; init; }
    /// <summary>대상 확장자(소문자, 점 포함). 기본 [".exe", ".dll"].</summary>
    public IReadOnlyList<string> IncludeExtensions { get; init; } = [".exe", ".dll"];
    /// <summary>하위 디렉토리 재귀 포함 여부. 기본 true.</summary>
    public bool Recursive { get; init; } = true;
    /// <summary>Authenticode 서명 검사 수행 여부. 기본 false.</summary>
    public bool VerifySignatures { get; init; }
    /// <summary>매니페스트 자기 자신 파일은 검사에서 제외(항상 true 권장).</summary>
    public bool ExcludeManifestFile { get; init; } = true;
}
```

> **확장자 정책**: 요구사항이 ".exe/.dll 등"이므로 기본 `.exe/.dll`에 더해 사용자가 옵션으로 확장 가능(예: `.pdb`, `.json`, `.config`는 의도적으로 기본 제외 — 빌드마다 바뀌어 오탐 유발). `IncludeExtensions`가 빈 목록이면 "모든 파일"로 해석한다.

---

## 4. 서비스 인터페이스 계약

```csharp
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 설치 디렉토리 바이너리 무결성 검사. SHA256 기반.
/// 매니페스트 영속: {대상 디렉토리}\integrity.manifest.json (기본). 직렬화: AgentJson.Options.
/// </summary>
public interface IBinaryIntegrityService
{
    /// <summary>현재 앱 설치 디렉토리(AppDomain.CurrentDomain.BaseDirectory)를 반환.</summary>
    string GetDefaultTargetDirectory();

    /// <summary>
    /// 대상 디렉토리에 대한 기본 매니페스트 경로를 반환.
    /// (기본: Path.Combine(targetDirectory, "integrity.manifest.json"))
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
```

### 4.1 (선택) 서명 검증 계약

```csharp
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>Authenticode 서명 검증(Windows 전용). 미주입(null)이면 서명 검사 비활성.</summary>
public interface IAuthenticodeVerifier
{
    /// <summary>파일의 Authenticode 서명 신뢰 상태를 반환. 예외 없이 SignatureStatus로 흡수.</summary>
    SignatureStatus Verify(string filePath);
}
```

### 4.2 구현 노트 (BinaryIntegrityService — ServiceEngineer용)
- **해싱**: `using var sha = SHA256.Create();` + `await sha.ComputeHashAsync(fileStream, ct)`. 스트림은
  `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize, useAsync:true)`.
  hex 변환은 `Convert.ToHexString(...)`(대문자) 사용.
- **열거**: `Directory.EnumerateFiles(root, "*", Recursive ? AllDirectories : TopDirectoryOnly)` 후 확장자 필터. 매니페스트 파일 및 `IncludeExtensions` 필터 적용. 상대경로는 `Path.GetRelativePath(root, full).Replace('\\','/')` 로 정규화.
- **비교 분류 알고리즘**:
  1. 디스크 파일 집합 D(상대경로→실파일), 매니페스트 엔트리 집합 M(상대경로→기대해시) 구성. 경로 비교는 `StringComparer.OrdinalIgnoreCase`(Windows).
  2. `M`의 각 엔트리: 디스크에 없으면 `Missing`. 있으면 해시 계산 → 실패 시 `Corrupted`(Detail에 사유) → 일치 `Ok` / 불일치 `Modified`.
  3. `D \ M`(매니페스트에 없는 디스크 파일): `Unexpected`.
  4. 카운트 집계 후 `IntegrityScanResult` 빌드.
- **진행률**: TotalFiles = (검사 대상 파일 수). 파일 처리마다 `progress?.Report(new IntegrityProgress{...})`. 보고 빈도는 파일당 1회.
- **취소**: 해싱 루프/`ComputeHashAsync`에 `ct` 전달, 루프 진입마다 `ct.ThrowIfCancellationRequested()`.
- **무거운 작업**: 기존 컨벤션대로 `await Task.Run(..., ct).ConfigureAwait(false)` 래핑(혹은 `ComputeHashAsync`가 이미 비동기이므로 직접 await + 열거만 Task.Run). 직렬화는 `AgentJson.Options` 재사용.
- **저장**: `tmp = path + ".tmp"; File.WriteAllText/WriteAllBytes(tmp); File.Move(tmp, path, overwrite:true);` (ChatHistoryService와 동일 원자적 패턴).
- **예외 정책**: 개별 파일 오류 → `FileIntegrityResult(Corrupted, Detail=메시지)`로 흡수(스캔 중단 금지). 매니페스트 부재/직렬화 치명 오류 → `AgentException`.

---

## 5. ViewModel 명세

### 5.1 `IntegrityViewModel`
생성자: `IntegrityViewModel(IBinaryIntegrityService integrity)` (서명 검증기는 서비스 내부 주입 사용; VM은 서비스만 의존).

| 속성 (`[ObservableProperty]`) | 타입 | 설명 |
|---|---|---|
| `TargetDirectory` | `string` | 검사 대상. 초기값 `integrity.GetDefaultTargetDirectory()`. |
| `IsScanning` | `bool` | 스캔 진행 중. 커맨드 CanExecute/UI 잠금 제어. |
| `ProgressFraction` | `double` | 0~1, ProgressBar 바인딩. |
| `ProgressText` | `string` | "123 / 456 — Foo.dll" 형태 상태 텍스트. |
| `CurrentFile` | `string?` | 현재 처리 파일. |
| `StatusMessage` | `string` | 결과/안내 메시지("매니페스트 없음 — 먼저 기준 생성" 등). |
| `HasManifest` | `bool` | 매니페스트 존재 여부(버튼 활성 제어). |
| `RecursiveOption` | `bool` | 재귀 검사 옵션(기본 true). |
| `VerifySignaturesOption` | `bool` | 서명 검사 옵션(기본 false). |
| `IncludeExtensionsText` | `string` | "exe,dll" 형태 입력(파싱해 옵션 빌드). |
| `Result` | `IntegrityScanResult?` | 마지막 스캔 결과(요약 바인딩 소스). |
| `OkCount` / `ModifiedCount` / `CorruptedCount` / `MissingCount` / `UnexpectedCount` | `int` | 요약 카운트(또는 `Result`에서 파생 프로퍼티로 노출). |
| `IsIntact` | `bool` (파생) | 요약 배지 색상 결정용. |

| 컬렉션 | 타입 | 설명 |
|---|---|---|
| `Files` | `ObservableCollection<FileIntegrityItemViewModel>` | 결과 그리드 바인딩(스캔 완료 후 채움). |
| `StatusFilter` | `IntegrityStatus?` (옵션) | 그리드 필터(전체/변조만/누락만 등). |

| 커맨드 (`[RelayCommand]`) | 시그니처 | CanExecute | 동작 |
|---|---|---|---|
| `ScanCommand` | `async Task ScanAsync()` | `!IsScanning && HasManifest` | 옵션 빌드 → `VerifyAsync(opts, null, progress, _cts.Token)` → `Files`/요약 채움. |
| `GenerateBaselineCommand` | `async Task GenerateBaselineAsync()` | `!IsScanning` | `GenerateBaselineAsync(...)` → 저장 → `HasManifest=true`. (덮어쓰기 확인은 View 측 MessageBox.) |
| `CancelCommand` | `void Cancel()` | `IsScanning` | `_cts?.Cancel()`. |
| `BrowseTargetCommand` | `void BrowseTarget()` 또는 View 코드비하인드에서 폴더 다이얼로그 후 `SetTargetDirectory(path)` | `!IsScanning` | 대상 디렉토리 선택(`bin`/`obj` 검사 지원). |
| `OpenManifestLocationCommand` | `void OpenManifestLocation()` | `HasManifest` | 탐색기로 매니페스트 폴더 열기(선택). |

상태/취소 관리:
- `private CancellationTokenSource? _cts;` — Scan/Baseline 시작 시 새로 생성, 완료/취소 시 dispose & null.
- `partial void OnIsScanningChanged(bool v)` 에서 각 커맨드 `NotifyCanExecuteChanged()` 호출.
- `IProgress<IntegrityProgress>`는 `new Progress<IntegrityProgress>(p => { ProgressFraction=p.Fraction; ProgressText=...; CurrentFile=p.CurrentFile; })`로 UI 스레드 마샬링(생성자에서 UI 스레드에 생성).
- `OperationCanceledException` catch → `StatusMessage = "검사 취소됨"`. `AgentException`/기타 → `StatusMessage = "오류: {msg}"`.
- 초기화: `public async Task InitializeAsync()` 에서 `HasManifest = integrity.ManifestExists(TargetDirectory)` 갱신(SettingsViewModel.InitializeAsync 패턴 동일).

### 5.2 `FileIntegrityItemViewModel`
- 생성자: `FileIntegrityItemViewModel(FileIntegrityResult model)`.
- 노출 속성(표시 전용, 대부분 읽기전용 getter): `RelativePath`, `Status`, `StatusText`(한글화: 정상/변조/손상/누락/추가), `ExpectedSha256Short`(앞 12자), `ActualSha256Short`, `Detail`, `SignatureText`, `StatusBrushKey`(상태→리소스키 문자열, 컨버터 최소화용).
- 정렬 우선순위: 문제 항목(Modified/Corrupted/Missing/Unexpected) 우선 → 그 다음 Ok. VM이 정렬해서 컬렉션에 추가.

---

## 6. View(XAML) 구성 요소 개요 — `IntegrityWindow.xaml`

`SettingsWindow.xaml` 스타일을 그대로 차용한다: `WindowStyle=None`, `AllowsTransparency=True`, 커스텀 타이틀바(드래그/닫기 버튼), 공유 StaticResource 테마.

레이아웃(Grid, 위→아래):
1. **타이틀바** (Row Height=40): 아이콘 + "무결성 검사" 타이틀 + 닫기(✕). `MouseLeftButtonDown`으로 드래그(코드비하인드).
2. **대상/옵션 영역**:
   - `TextBox` (TargetDirectory, ReadOnly 권장) + "찾아보기" `Button`(BrowseTarget).
   - `CheckBox` 재귀(RecursiveOption), `CheckBox` 서명 검사(VerifySignaturesOption).
   - `TextBox` 확장자(IncludeExtensionsText, placeholder "exe,dll").
3. **액션 바**: `Button` 검사 시작(ScanCommand), `Button` 기준 생성(GenerateBaselineCommand), `Button` 취소(CancelCommand, IsScanning일 때만 보임).
4. **진행률**: `ProgressBar` (Value=ProgressFraction, Minimum=0 Maximum=1) + `TextBlock`(ProgressText). `Visibility`는 IsScanning에 바인딩(BoolToVisibility 컨버터).
5. **요약 배지 영역**: 정상/변조/손상/누락/추가 카운트를 색상 칩으로 표시(IsIntact면 녹색 "무결성 양호", 아니면 적색 경고).
6. **결과 그리드**: `DataGrid`(또는 `ListView` + GridView) — 컬럼: 상태(색상 점/텍스트), 상대경로, 기대해시(짧게), 실제해시(짧게), 크기, 서명, 비고. `ItemsSource={Binding Files}`, 상태별 행 색상은 `DataTrigger` 또는 `FileIntegrityItemViewModel.StatusBrushKey`.
7. **상태표시줄**: `TextBlock`(StatusMessage).

코드비하인드(`IntegrityWindow.xaml.cs`)는 최소화: 타이틀바 드래그, 닫기, 폴더 선택 다이얼로그(WPF에 기본 폴더 다이얼로그 없으므로 `System.Windows.Forms.FolderBrowserDialog` 사용 — 프로젝트가 이미 `UseWindowsForms=true`), 그리고 `Loaded`에서 `await vm.InitializeAsync()`.

컨버터: `BoolToVisibilityConverter`(기존 존재 가능, 없으면 `Views/Converters.cs`에 추가), 필요 시 `IntegrityStatusToBrushConverter`. **권장**: 상태 표현은 `FileIntegrityItemViewModel`의 표시 속성으로 처리해 컨버터를 최소화.

---

## 7. App.xaml.cs 수동 DI 등록 안내

`App.OnStartup` 내, 기존 `var chatHistory = new ChatHistoryService();` 부근(9b 블록)에 추가:

```csharp
// (신규) 바이너리 무결성 검사 서비스
//  - 서명 검증기는 선택: Windows에서만 실제 구현 주입, 미사용 시 null 전달.
IAuthenticodeVerifier? authenticode = new AuthenticodeVerifier();   // 선택
var binaryIntegrity = new BinaryIntegrityService(authenticode);     // 또는 new BinaryIntegrityService()
```

진입점 노출 방식(택1, UIDesigner/Orchestrator 결정):
- **(A) 트레이 컨텍스트 메뉴**: `InitializeTrayIcon()`의 "Settings" 항목 패턴을 복제해 "무결성 검사" `ToolStripMenuItem` 추가 →
  `var vm = new IntegrityViewModel(binaryIntegrity); var win = new IntegrityWindow(vm); win.Show();`
- **(B) 설정 화면 내 버튼**: SettingsWindow에서 열기.

> `binaryIntegrity`/`authenticode`를 `App`의 필드로 보관(다른 서비스와 동일 패턴)하면 메뉴 핸들러에서 재사용 가능. 필드 추가 위치: 상단 `private I... ?` 필드 블록.

---

## 8. 매니페스트 파일 포맷 (JSON 스키마) 및 저장 위치

### 8.1 저장 위치
- 기본: `Path.Combine(targetDirectory, "integrity.manifest.json")`.
  - 즉, 설치 디렉토리 검사 시 설치 폴더 루트에 `integrity.manifest.json`이 놓인다.
  - 매니페스트 파일 자신은 항상 검사 대상에서 제외(`IntegrityScanOptions.ExcludeManifestFile`).
- 직렬화기: `System.Text.Json` + `AgentJson.Options`(snake_case, null 생략). (프로젝트 기존 record 직렬화와 일치.)

> **보안 주의(설계 메모)**: 매니페스트를 검사 대상 폴더 내부에 두면 변조자가 매니페스트도 함께 갱신할 수 있다(자기서명 위조). 강한 무결성 보장이 필요하면 후속 단계에서 (a) 매니페스트를 `%APPDATA%\OhMyAgent\integrity\{경로해시}.manifest.json`에 보관, (b) 매니페스트 자체에 서명/HMAC 적용을 고려한다. 본 1차 설계는 "탐지(detection)" 목적이므로 폴더 내 저장을 기본값으로 하되, 위 옵션을 §9/확장 포인트로 남긴다.

### 8.2 JSON 예시
```json
{
  "schema_version": 1,
  "created_utc": "2026-06-23T12:34:56.0000000+00:00",
  "root_label": "OhMyAgent.AiAgent.Client (install)",
  "algorithm": "SHA256",
  "entries": [
    {
      "relative_path": "OhMyAgent.AiAgent.Client.exe",
      "sha256": "A1B2C3...64HEX...",
      "size": 184320
    },
    {
      "relative_path": "CommunityToolkit.Mvvm.dll",
      "sha256": "9F8E7D...64HEX...",
      "size": 245760
    }
  ]
}
```

### 8.3 스키마 규칙
- `relative_path`: 매니페스트 위치(=targetDirectory) 기준, 항상 `/` 구분, 대소문자 원본 보존하되 비교는 OrdinalIgnoreCase.
- `sha256`: 대문자 hex 64자.
- `size`: 음이 아닌 long.
- `schema_version` 불일치(미래 버전) → 로드 시 경고 후 재생성 권장(`StatusMessage`).

---

## 9. 엣지 케이스

| # | 상황 | 처리 |
|---|---|---|
| 1 | **매니페스트 없음** | `VerifyAsync`가 로드 실패 → `AgentException`. VM은 catch 후 `StatusMessage="매니페스트 없음 — '기준 생성'을 먼저 실행하세요"`, ScanCommand는 `HasManifest=false`로 비활성. |
| 2 | **매니페스트 손상(역직렬화 실패)** | `LoadManifestAsync`가 `Debug.WriteLine` 후 null. VM은 "매니페스트 손상 — 재생성 필요" 안내. |
| 3 | **파일 잠김/사용 중(자기 자신 .exe/.dll 포함)** | `FileShare.Read|Delete`로 열기 시도. 그래도 실패하면 해당 파일 `Corrupted` + `Detail="파일 잠김/읽기 실패"`. 스캔 전체는 계속. (실행 중인 자기 exe/로드된 dll은 보통 공유 읽기 가능.) |
| 4 | **접근 거부(권한 없음, UnauthorizedAccessException)** | 개별 파일 → `Corrupted`/`Detail="접근 거부"`. 디렉토리 열거 중 접근 거부 → 해당 하위 스킵 + Debug 로그(전체 중단 금지). |
| 5 | **자기 자신(매니페스트 파일) 검사** | 항상 제외(`ExcludeManifestFile`). |
| 6 | **대상 디렉토리 없음/경로 오류** | `VerifyAsync`/`GenerateBaselineAsync` 진입 시 `Directory.Exists` 확인 → 없으면 `AgentException("대상 디렉토리 없음")`. |
| 7 | **빈 디렉토리 / 대상 0개** | `IntegrityScanResult`(빈 Files, 카운트 0), `IsBaselineOnly`/`IsIntact` 적절히. UI는 "검사 대상 없음" 안내. |
| 8 | **취소 중간** | `OperationCanceledException` 전파 → VM `StatusMessage="검사 취소됨"`, 부분 결과는 폐기(혹은 부분 표시는 비범위). `_cts` dispose. |
| 9 | **대용량(수천 파일)** | 스트리밍 해싱(`ComputeHashAsync`)으로 메모리 일정. 진행률 보고는 파일당 1회. UI는 가상화 그리드 권장. |
| 10 | **누락 vs 추가 동시(파일 교체)** | 같은 상대경로면 `Modified`로 분류(누락+추가가 아님). 다른 경로면 각각 Missing/Unexpected. |
| 11 | **빌드 산출물 검사(obj/bin)** | 기본 확장자 `.exe/.dll`만 비교하므로 `.pdb`/임시파일 오탐 적음. 사용자가 확장자 조정 가능. baseline은 검사 시점 스냅샷이므로 재빌드 후 재생성 필요. |
| 12 | **서명 검사 실패/예외** | `IAuthenticodeVerifier.Verify`는 예외를 내부 흡수해 `SignatureStatus.Invalid/Unsigned` 반환. 해시 검증 결과를 절대 덮어쓰지 않음(부가 정보). 비주입(null)이면 전부 `NotChecked`. |
| 13 | **심볼릭 링크/재분석 지점** | 1차 범위: 일반 파일만. 링크 추적으로 인한 무한 루프 방지 위해 `EnumerateFiles` 기본 동작 사용(리파스 포인트 추적 안 함 권장). 깊은 처리 비범위. |
| 14 | **동시 재진입(스캔 중 재클릭)** | `IsScanning` 가드 + CanExecute로 차단. |

---

## 10. 의존성 다이어그램

```
IntegrityWindow.xaml (View)
        │  DataContext
        ▼
IntegrityViewModel ──owns──► ObservableCollection<FileIntegrityItemViewModel>
        │  depends on (ctor inject)
        ▼
IBinaryIntegrityService ──(impl)──► BinaryIntegrityService
                                          │ optional ctor inject
                                          ▼
                                   IAuthenticodeVerifier ──► AuthenticodeVerifier
        │ produces / consumes
        ▼
Models: IntegrityManifest, IntegrityManifestEntry, FileIntegrityResult,
        IntegrityScanResult, IntegrityProgress, IntegrityScanOptions,
        IntegrityStatus, SignatureStatus
        │ persisted as
        ▼
{targetDirectory}\integrity.manifest.json  (System.Text.Json / AgentJson.Options)

App.xaml.cs (수동 DI): new AuthenticodeVerifier() → new BinaryIntegrityService(...)
                       → (메뉴/설정 진입 시) new IntegrityViewModel(...) → new IntegrityWindow(vm)
```

규칙 준수: View→ViewModel→Service→Model 단방향. Model은 어떤 레이어도 참조하지 않음.

---

## 11. 생성 파일 전체 경로 (후속 에이전트 작업 목록)

**Models**
- `OhMyAgent.AiAgent.Client/Models/Integrity/IntegrityStatus.cs`
- `OhMyAgent.AiAgent.Client/Models/Integrity/SignatureStatus.cs` (선택)
- `OhMyAgent.AiAgent.Client/Models/Integrity/IntegrityManifestEntry.cs`
- `OhMyAgent.AiAgent.Client/Models/Integrity/IntegrityManifest.cs`
- `OhMyAgent.AiAgent.Client/Models/Integrity/FileIntegrityResult.cs`
- `OhMyAgent.AiAgent.Client/Models/Integrity/IntegrityScanResult.cs`
- `OhMyAgent.AiAgent.Client/Models/Integrity/IntegrityProgress.cs`
- `OhMyAgent.AiAgent.Client/Models/Integrity/IntegrityScanOptions.cs`

**Services**
- `OhMyAgent.AiAgent.Client/Services/IBinaryIntegrityService.cs`
- `OhMyAgent.AiAgent.Client/Services/BinaryIntegrityService.cs`
- `OhMyAgent.AiAgent.Client/Services/IAuthenticodeVerifier.cs` (선택)
- `OhMyAgent.AiAgent.Client/Services/AuthenticodeVerifier.cs` (선택)

**ViewModels**
- `OhMyAgent.AiAgent.Client/ViewModels/IntegrityViewModel.cs`
- `OhMyAgent.AiAgent.Client/ViewModels/FileIntegrityItemViewModel.cs`

**Views**
- `OhMyAgent.AiAgent.Client/Views/IntegrityWindow.xaml`
- `OhMyAgent.AiAgent.Client/Views/IntegrityWindow.xaml.cs`
- (필요 시) `OhMyAgent.AiAgent.Client/Views/Converters.cs` 에 컨버터 추가

**수정**
- `OhMyAgent.AiAgent.Client/App.xaml.cs` — 서비스 수동 등록 + 진입점(트레이 메뉴 또는 설정 버튼).

> 네임스페이스: Models 하위 폴더 `Integrity`를 쓰더라도 네임스페이스는 프로젝트 컨벤션상 `OhMyAgent.AiAgent.Client.Models`로 유지(기존 `Models/Agent/AgentEnums.cs`가 폴더와 무관하게 `.Models` 사용하는 패턴과 동일).

---

## 12. 구현 제외 범위 (이번 설계에서 다루지 않음)

- 매니페스트 자체의 디지털 서명/HMAC 보호(자기위조 방지) — §8.1 보안 메모로만 남김.
- 매니페스트를 `%APPDATA%`로 옮기는 대안 저장 위치(확장 포인트).
- 자동 복구/재다운로드/롤백 (탐지만, 복구 없음).
- 실시간 파일 감시(FileSystemWatcher 기반 상시 모니터링).
- 증분/델타 검증 캐시(매 실행 전량 해싱).
- 비-Windows 플랫폼(net10.0-windows 전용).
- 멀티 해시 알고리즘 선택 UI(SHA256 고정; `algorithm` 필드는 미래 대비 메타만).
- 심볼릭 링크/정션 깊은 추적.

---

## 13. 엔지니어 분배 요약

| 에이전트 | 담당 |
|---|---|
| ServiceEngineer | §3 모델 8종 + §4 `IBinaryIntegrityService`/`BinaryIntegrityService`(+선택 Authenticode). §4.2 구현 노트 준수. |
| ViewModelEngineer | §5 `IntegrityViewModel`, `FileIntegrityItemViewModel`. 진행률 마샬링/취소/CanExecute. |
| UIDesigner | §6 `IntegrityWindow.xaml(.cs)` + 컨버터. SettingsWindow 테마 차용. |
| (Orchestrator) | §7 App.xaml.cs 수동 등록 + 진입점, §11 파일 생성 조율. |
| QAReviewer | MVVM 단방향/바인딩 정합/취소·진행률·null 안전성/원자적 저장 검증. |
