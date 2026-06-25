# 02. Service Summary — 바이너리 무결성 검사 Model + Service 레이어

> 작성자: ServiceEngineer 에이전트
> 담당 범위: §3 모델 8종 + §4 `IBinaryIntegrityService`/`BinaryIntegrityService`(+선택 Authenticode)
> 빌드 검증 미수행(병렬 작업). 컴파일 가능 코드 작성에 집중.

---

## 1. 생성 완료 파일

### Models (네임스페이스: `OhMyAgent.AiAgent.Client.Models` — 폴더가 Integrity여도 동일)
- `Models/Integrity/IntegrityStatus.cs` — `enum` (Ok/Modified/Corrupted/Missing/Unexpected)
- `Models/Integrity/SignatureStatus.cs` — `enum` (NotChecked/Valid/Invalid/Unsigned)
- `Models/Integrity/IntegrityManifestEntry.cs` — `sealed record` (`[JsonPropertyName(snake_case)]`)
- `Models/Integrity/IntegrityManifest.cs` — `sealed record` (JSON 직렬화 대상)
- `Models/Integrity/FileIntegrityResult.cs` — `sealed record`
- `Models/Integrity/IntegrityScanResult.cs` — `sealed record` (요약 카운트 + 파생 `IsIntact`)
- `Models/Integrity/IntegrityProgress.cs` — `readonly record struct` (파생 `Fraction`)
- `Models/Integrity/IntegrityScanOptions.cs` — `sealed record`

### Services (네임스페이스: `OhMyAgent.AiAgent.Client.Services`)
- `Services/IBinaryIntegrityService.cs` — `interface`
- `Services/BinaryIntegrityService.cs` — `sealed class : IBinaryIntegrityService`
- `Services/IAuthenticodeVerifier.cs` — `interface` (선택)
- `Services/AuthenticodeVerifier.cs` — `sealed class : IAuthenticodeVerifier` (WinVerifyTrust P/Invoke)

---

## 2. 모델 공개 API (ViewModel 바인딩 참조용)

### enum `IntegrityStatus`
`Ok, Modified, Corrupted, Missing, Unexpected`

### enum `SignatureStatus`
`NotChecked, Valid, Invalid, Unsigned`

### `IntegrityManifestEntry` (sealed record)
- `required string RelativePath`  — '/' 구분 상대경로
- `required string Sha256`        — 대문자 hex 64자
- `long Size`

### `IntegrityManifest` (sealed record)
- `int SchemaVersion` = 1
- `DateTimeOffset CreatedUtc`
- `string? RootLabel`
- `string Algorithm` = "SHA256"
- `IReadOnlyList<IntegrityManifestEntry> Entries` = []

### `FileIntegrityResult` (sealed record) — 그리드 1행 소스
- `required string RelativePath`
- `required IntegrityStatus Status`
- `string? ExpectedSha256`  — Unexpected면 null
- `string? ActualSha256`    — Missing/Corrupted면 null
- `long? ActualSize`
- `SignatureStatus Signature` = NotChecked
- `string? Detail`          — 오류/부가 설명("해시 불일치", "파일 없음", "접근 거부" 등)

### `IntegrityScanResult` (sealed record) — 요약 바인딩 소스
- `required IReadOnlyList<FileIntegrityResult> Files`
- `required DateTimeOffset ScannedUtc`
- `required string TargetDirectory`
- `bool IsBaselineOnly`
- `int OkCount / ModifiedCount / CorruptedCount / MissingCount / UnexpectedCount`
- `bool IsIntact` (파생, getter 전용) = Modified==0 && Corrupted==0 && Missing==0 && Unexpected==0

### `IntegrityProgress` (readonly record struct) — `IProgress<T>` 페이로드
- `int ProcessedFiles`
- `int TotalFiles`
- `string? CurrentFile`
- `double Fraction` (파생, getter 전용) = TotalFiles<=0 ? 0 : Processed/Total  (0.0~1.0)

### `IntegrityScanOptions` (sealed record) — VM이 빌드해서 서비스에 전달
- `required string TargetDirectory`
- `IReadOnlyList<string> IncludeExtensions` = [".exe", ".dll"]  — 소문자+점 포함. **빈 목록이면 모든 파일**.
- `bool Recursive` = true
- `bool VerifySignatures` = false
- `bool ExcludeManifestFile` = true

> VM 옵션 빌드 시 주의: `IncludeExtensionsText`("exe,dll") 파싱 시 각 항목에 점을 붙여(".exe") 소문자로 정규화할 것. 점 없는 "exe"는 `Path.GetExtension`이 ".exe"를 반환하므로 매칭되지 않는다.

---

## 3. 서비스 인터페이스 계약 (ViewModelEngineer 필독)

### `IBinaryIntegrityService`
모든 메서드 시그니처 (CancellationToken은 항상 마지막, default):

| 메서드 | 시그니처 | 반환 | 비고 |
|---|---|---|---|
| `GetDefaultTargetDirectory` | `string GetDefaultTargetDirectory()` | `string` | `AppDomain.CurrentDomain.BaseDirectory`. 동기. |
| `GetManifestPath` | `string GetManifestPath(string targetDirectory)` | `string` | `{dir}/integrity.manifest.json`. 동기. |
| `ManifestExists` | `bool ManifestExists(string targetDirectory)` | `bool` | 동기. null/공백이면 false. |
| `LoadManifestAsync` | `Task<IntegrityManifest?> LoadManifestAsync(string targetDirectory, CancellationToken ct = default)` | `Task<IntegrityManifest?>` | 없거나 손상 시 **null**(예외 아님). |
| `GenerateBaselineAsync` | `Task<IntegrityScanResult> GenerateBaselineAsync(IntegrityScanOptions options, IProgress<IntegrityProgress>? progress = null, CancellationToken ct = default)` | `Task<IntegrityScanResult>` | 매니페스트 생성+원자적 저장. 결과 `IsBaselineOnly=true`, 정상 파일은 Status=Ok. |
| `VerifyAsync` | `Task<IntegrityScanResult> VerifyAsync(IntegrityScanOptions options, IntegrityManifest? manifest = null, IProgress<IntegrityProgress>? progress = null, CancellationToken ct = default)` | `Task<IntegrityScanResult>` | manifest null이면 디스크에서 로드 시도. 그래도 없으면 **AgentException**. |
| `ComputeSha256Async` | `Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)` | `Task<string>` | 대문자 hex. 실패 시 **AgentException**. |

### `IAuthenticodeVerifier` (선택)
- `SignatureStatus Verify(string filePath)` — 동기, 예외 없이 흡수. **VM은 이 인터페이스를 직접 의존하지 않음** (서비스 내부 주입).

---

## 4. DI 등록에 필요한 생성자 시그니처 (App.xaml.cs — Orchestrator용)

```csharp
// 서명 검증기는 선택: null 가능. Windows 전용 WinVerifyTrust 사용.
IAuthenticodeVerifier? authenticode = new AuthenticodeVerifier();   // 또는 null
IBinaryIntegrityService binaryIntegrity = new BinaryIntegrityService(authenticode);
// 서명 검사 불필요 시: new BinaryIntegrityService()  (authenticode 기본값 null)
```

- `AuthenticodeVerifier()` — 매개변수 없는 생성자.
- `BinaryIntegrityService(IAuthenticodeVerifier? authenticode = null)` — 선택 주입. null이면 서명 검사 전부 `NotChecked`.

ViewModel 생성자 (참고, ViewModelEngineer 담당):
```csharp
var vm = new IntegrityViewModel(binaryIntegrity);   // 서비스만 의존
```

---

## 5. 동작/계약 세부 (VM 처리 분기에 영향)

- **취소**: 취소 시 `OperationCanceledException` 전파(흡수 안 함). VM에서 catch → `StatusMessage="검사 취소됨"`. 부분 결과는 반환되지 않음.
- **매니페스트 없음**: `VerifyAsync`는 `AgentException("매니페스트 없음 — '기준 생성'을 먼저 실행하세요.")` throw. VM은 catch → 안내.
- **매니페스트 손상**: `LoadManifestAsync`는 null 반환(Debug 로그). VM이 미리 로드해 null이면 "손상 — 재생성" 안내 후, `VerifyAsync(opts, null, ...)` 호출 시 다시 AgentException 발생하므로 분기 주의.
- **대상 디렉토리 없음**: `VerifyAsync`/`GenerateBaselineAsync` 진입 시 `Directory.Exists` 확인 → `AgentException("대상 디렉토리 없음: {root}")`.
- **개별 파일 오류**: 스캔 중단 안 함. 읽기 실패 → `Corrupted` + `Detail`("파일 잠김/읽기 실패", "접근 거부"). 접근 거부 하위 디렉토리는 열거에서 자동 스킵(`EnumerationOptions.IgnoreInaccessible=true`).
- **누락+추가 동일 경로(파일 교체)**: 같은 상대경로면 해시 비교로 `Modified` 처리됨(설계대로).
- **빈 디렉토리/대상 0개**: 빈 `Files`, 카운트 0, `IsIntact=true`. VM은 "검사 대상 없음" 안내 가능.
- **진행률 보고**: 파일당 1회 `progress.Report`. `VerifyAsync`의 TotalFiles = (매니페스트 엔트리 수 + Unexpected 디스크 파일 수). `GenerateBaselineAsync`의 TotalFiles = 대상 파일 수.
- **서명 검사 옵션 OFF 또는 verifier 미주입**: 모든 `Signature = NotChecked`.

---

## 6. 구현 결정 / 명세와의 차이 기록

1. **해싱 API**: 명세 §4.2는 `SHA256.Create()` + `ComputeHashAsync`를 예시로 들었으나, 더 간결하고 동등한 `SHA256.HashDataAsync(stream, ct)`(static, 인스턴스 disposal 불필요)를 사용. 결과 동일(대문자 hex, `Convert.ToHexString`).
2. **디렉토리 열거**: `Directory.EnumerateFiles` + 수동 try/catch 대신 `EnumerationOptions { IgnoreInaccessible=true, AttributesToSkip=ReparsePoint }` 사용 → 접근 거부 하위 자동 스킵 + 심볼릭 링크/정션 미추적(§9 #4, #13 충족)을 한 번에 처리.
3. **AuthenticodeVerifier**: `X509Certificate.CreateFromSignedFile`(존재만 확인) 대신 신뢰 체인까지 검증하는 **WinVerifyTrust(wintrust.dll) P/Invoke**로 구현. HRESULT를 `SignatureStatus`로 매핑(SUCCESS→Valid, TRUST_E_NOSIGNATURE/SUBJECT_FORM_UNKNOWN/PROVIDER_UNKNOWN→Unsigned, 그 외→Invalid). 모든 예외 내부 흡수. 비-Windows에서는 `NotChecked` 반환.
4. **이중 안전망**: 서비스 내부 `VerifySignature`에서도 verifier 호출을 try/catch로 한 번 더 감쌈(verifier가 계약을 어겨 예외를 던져도 스캔 중단 방지).
5. `BinaryIntegrityService.ManifestFileName`(public const = "integrity.manifest.json") 노출 — 필요 시 VM/View에서 참조 가능.

---

## 7. ViewModelEngineer 체크리스트 (계약 요약)

- `using OhMyAgent.AiAgent.Client.Models;` + `using OhMyAgent.AiAgent.Client.Services;`
- 생성자 주입: `IBinaryIntegrityService` 1개만.
- `IProgress<IntegrityProgress>`는 UI 스레드에서 `new Progress<>(...)` 생성(생성자에서). 콜백에서 `ProgressFraction=p.Fraction`, `ProgressText=$"{p.ProcessedFiles} / {p.TotalFiles} — {p.CurrentFile}"`, `CurrentFile=p.CurrentFile`.
- 예외 분기: `OperationCanceledException`(취소), `AgentException`(도메인 오류 — msg 표시), 기타 일반 예외.
- 옵션 빌드: 확장자 텍스트 파싱 시 점 접두 + 소문자 정규화 필수(5절/§2 참고).
```
