# 05. QA 검증 리포트 — 바이너리 무결성 검사 기능

> 작성자: QAReviewer 에이전트
> 검증 대상: Models(8) / Services(4) / ViewModels(2) / Views(1+cs) / Colors.xaml / App.xaml.cs
> 입력: 01_architect_spec / 03_viewmodel_summary / 04_ui_summary (02_service_summary 부재)

---

## 1. 종합 판정: **PASS**

빌드 0 error. 서비스↔VM 계약, XAML 바인딩, MVVM 단방향, 비동기/취소/진행률, null 안전성 전 항목 정합 확인. 빌드를 막던 2건의 컴파일 오류는 직접 수정 완료.

---

## 2. 빌드 결과

| 항목 | 값 |
|---|---|
| 명령어 | `"/mnt/c/Program Files/dotnet/dotnet.exe" build OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client.csproj -c Debug` |
| 최종 결과 | **빌드 성공 (오류 0개)** |
| 경고 | 5개 — 모두 기능 외 기존 경고 |

### 경고 상세 (모두 본 기능과 무관, 블로커 아님)
- `NU1510`: `System.Drawing.Common` 패키지 prune 안내 (프로젝트 전역, 기존).
- `CS8767` (×): `Services/ToolRegistry.cs(26)` `TryGet` out 파라미터 nullable 불일치 (기존 코드, 본 기능 무관).

> 무결성 기능 관련 신규 경고는 **0개**.

### 초기 빌드에서 발견되어 수정한 오류 2건 (아래 §4)
1. `MC3024` — IntegrityWindow.xaml(238): `Border.Style` 중복 설정.
2. `CS0104` — IntegrityWindow.xaml.cs(57): `MessageBox` 모호 참조(WinForms vs WPF).

---

## 3. 항목별 검증 결과

### 3.1 서비스 ↔ VM 계약 정합성 (최우선) — PASS
`IBinaryIntegrityService`의 7개 멤버를 VM 호출부와 1:1 대조:

| 인터페이스 시그니처 | VM 호출부 | 정합 |
|---|---|---|
| `GetDefaultTargetDirectory()` | 생성자: `TargetDirectory = _integrity.GetDefaultTargetDirectory()` | ✅ |
| `GetManifestPath(string)` | `GetManifestPath()` → `_integrity.GetManifestPath(TargetDirectory)` | ✅ |
| `ManifestExists(string)` | 생성자/`RefreshManifestState()` | ✅ |
| `LoadManifestAsync(string, ct)` | (VM 미사용 — VerifyAsync 내부 로드) | ✅ (의도적) |
| `GenerateBaselineAsync(options, progress?, ct)` | `RunScanAsync`: `GenerateBaselineAsync(options, _progress, token)` | ✅ optional 3인자 일치 |
| `VerifyAsync(options, manifest?, progress?, ct)` | `VerifyAsync(options, null, _progress, token)` | ✅ manifest=null 전달 |
| `ComputeSha256Async(string, ct)` | (VM 미사용, 보조 API) | ✅ |

반환 타입 `IntegrityScanResult`의 VM 소비(`OkCount`/`ModifiedCount`/`CorruptedCount`/`MissingCount`/`UnexpectedCount`/`IsIntact`/`Files`)와 `IntegrityProgress`(`Fraction`/`ProcessedFiles`/`TotalFiles`/`CurrentFile`) 필드 모두 모델 정의와 일치. `IntegrityScanOptions` 빌드(`TargetDirectory`/`IncludeExtensions`/`Recursive`/`VerifySignatures`/`ExcludeManifestFile`) 정합.

### 3.2 XAML 바인딩 정합성 — PASS
- VM 바인딩(`TargetDirectory`, `RecursiveOption`, `VerifySignaturesOption`, `IncludeExtensionsText`, `IsScanning`, `ProgressFraction`, `ProgressText`, `HasResult`, `HasManifest`, `IsIntact`, `Ok/Modified/Corrupted/Missing/UnexpectedCount`, `Files`, `ScanCommand`, `CancelCommand`, `StatusMessage`) — 모두 VM 실제 public 멤버에 존재. `[RelayCommand]` → `ScanCommand`/`CancelCommand` 자동 생성 확인.
- 행 바인딩(`Status`, `StatusText`, `RelativePath`, `ExpectedSha256Short`, `ActualSha256Short`, `SizeText`, `SignatureText`, `Detail`) — `FileIntegrityItemViewModel` 멤버와 일치.
- `x:Static models:IntegrityStatus.{Ok,Modified,Corrupted,Missing,Unexpected}` — `models:` = `OhMyAgent.AiAgent.Client.Models`, enum 위치 일치 → 빌드 통과로 해석 검증됨.
- 리소스 키 전수 확인: 브러시 5종(`IntegrityOk/Modified/Corrupted/Missing/UnexpectedBrush`) Colors.xaml 존재. 스타일(`FloatingCard`, `PrimaryButton`, `OutlineButton`, `CaptionButton`, `DarkTextBox`, `Chip`) 및 폰트(`AppFont`, `MonoFont`), 컨버터(`BoolToVisibility`, `InverseBool`), 색상(`AccentSoftBrush`, `AccentSubtleBrush`, `Surface2Bg`, `Surface3Bg`, `AccentBrush`, `TextMuted` 등) 모두 Resources에 존재.

### 3.3 MVVM 패턴 — PASS
- 단방향 의존(View→VM→Service→Model) 준수. VM에 `Window`/`UIElement`/`Dispatcher`/`MessageBox` 참조 없음.
- 다이얼로그/셸은 코드비하인드가 소유하고 `SetTargetDirectory`/`GetManifestPath`/`GenerateBaselineCommand` public 멤버에 위임 — 게이트 패턴 정확.
- `sealed partial class : ObservableObject` + `[ObservableProperty]`/`[RelayCommand]` 소스 생성기 정합.
- `MessageBox.Show`는 View 코드비하인드에만 존재(설계 §5.1 허용 — 덮어쓰기 확인).

### 3.4 비동기 / 취소 / 진행률 — PASS
- `_cts` 생명주기: `RunScanAsync` 진입 시 기존 dispose 후 새로 생성, `finally`에서 dispose & null. 정상.
- `OperationCanceledException` → "검사 취소됨", `AgentException`/일반 예외 → "오류: {msg}" 분기 정확.
- `IProgress<IntegrityProgress>`를 생성자(UI 스레드)에서 `Progress<T>`로 생성 → 콜백 UI 스레드 마샬링. `OnProgress`에서 직접 프로퍼티 갱신(Dispatcher 직접 호출 없음). 정확.
- 서비스 측: 해싱 루프 `ct.ThrowIfCancellationRequested()` + `SHA256.HashDataAsync(stream, ct)` 취소 전달. `async void`는 `IntegrityWindow_Loaded` 이벤트 핸들러뿐(허용).

### 3.5 null 안전성 / 메모리 누수 — PASS
- Nullable enable 환경에서 신규 코드 nullable 경고 0.
- `FileStream`/SHA256는 `await using`/`HashDataAsync`로 누수 없음. `AllocHGlobal`은 `finally`에서 `FreeHGlobal`, WinVerifyTrust 상태 핸들도 `WTD_STATEACTION_CLOSE`로 정리.
- 이벤트 구독: VM은 서비스/외부 이벤트 구독 없음(`Loaded`만 View 측). static 이벤트 핸들러 누수 없음 → IDisposable 불필요.

### 3.6 빌드 검증 — PASS
실제 WSL→Windows dotnet으로 빌드. 최초 2 error 발견 후 직접 수정, 재빌드 0 error 확인.

---

## 4. 직접 수정한 항목

| 파일:라인 | 문제(심각도) | 수정 내용 |
|---|---|---|
| `Views/IntegrityWindow.xaml:238` | `MC3024` Border.Style 중복(Critical, 빌드 실패) | 종합 배지 `Border`에서 attribute `Style="{StaticResource Chip}"`와 `Padding="14,7"`를 제거. 인라인 `<Border.Style>`(이미 `BasedOn=Chip`이며 Padding 14,7 Setter 포함)이 유일한 Style 지정이 되도록 정리. |
| `Views/IntegrityWindow.xaml.cs:1-9` | `CS0104` MessageBox 모호 참조(Critical, 빌드 실패) | `UseWindowsForms=true`로 `System.Windows.Forms`가 암시 import되어 `MessageBox`/`MessageBoxButton`/`MessageBoxImage`/`MessageBoxResult`가 WPF/WinForms 간 모호. 4개 심볼에 `using X = System.Windows.X;` 별칭 추가. |

---

## 5. 미해결 / 사용자 결정 필요 항목

없음. 모든 빌드 블로커는 해결됨.

---

## 6. 권장 후속 작업 (블로커 아님)

- **(Low) 매니페스트 자기위조 방지**: 설계 §8.1 보안 메모대로 현재 매니페스트가 검사 대상 폴더 내부에 저장되어 변조자가 함께 갱신 가능. 강한 보장이 필요하면 `%APPDATA%` 저장 또는 매니페스트 HMAC/서명을 후속 단계로 고려(설계상 의도적 1차 범위 제외).
- **(Low) `IncludeExtensionsText` 빈 입력 시 모든 파일 검사**: `bin`/`obj` 등 대상에서 `.pdb`/임시파일 대량 오탐 가능. UI 툴팁에 이미 안내됨. 동작은 설계 의도와 일치.
- **(Info) 기존 경고 정리(선택)**: `ToolRegistry.TryGet` CS8767, `System.Drawing.Common` NU1510 — 본 기능과 무관하나 별도 정리 권장.
- **(Info) `LoadManifestAsync`/`ComputeSha256Async`는 VM 미사용**: 인터페이스 계약상 보조 API로 유지(설계 §4 명시). 제거 불필요.
