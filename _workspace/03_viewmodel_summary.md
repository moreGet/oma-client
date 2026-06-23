# 03. ViewModel 레이어 구현 요약 — 바이너리 무결성 검사

> 작성자: ViewModelEngineer
> 담당: 명세 §5 `IntegrityViewModel`, `FileIntegrityItemViewModel`
> 서비스 계약 기준: `_workspace/02_service_summary.md` 부재 → **명세 §4 `IBinaryIntegrityService`** 기준으로 구현.
> 모델: §3 8종 모두 존재 확인(`Models/Integrity/*.cs`).

---

## 생성 파일

- `OhMyAgent.AiAgent.Client/ViewModels/IntegrityViewModel.cs`
- `OhMyAgent.AiAgent.Client/ViewModels/FileIntegrityItemViewModel.cs`

둘 다 `OhMyAgent.AiAgent.Client.ViewModels`, `sealed partial class : ObservableObject`, Nullable enable,
CommunityToolkit.Mvvm 8.3.2 소스 생성기(`[ObservableProperty]`, `[RelayCommand]`) 사용.

---

## IntegrityViewModel — XAML 바인딩 표면

### DataContext
`IntegrityWindow.xaml`의 DataContext = `IntegrityViewModel`. 생성자 주입: `IBinaryIntegrityService` (단일 의존성).
`new IntegrityViewModel(binaryIntegrity)`는 UI 스레드(트레이/설정 진입점)에서 생성해야 함 — `Progress<T>` 마샬링 전제.

### 바인딩 가능 속성 (양방향/입력)
| 바인딩 이름 | 타입 | 모드 | 용도 |
|---|---|---|---|
| `TargetDirectory` | string | OneWay 권장(읽기 표시) | 검사 대상 경로 TextBox. 변경 시 `HasManifest` 자동 재평가. |
| `RecursiveOption` | bool | TwoWay | 재귀 검사 CheckBox. |
| `VerifySignaturesOption` | bool | TwoWay | 서명 검사 CheckBox. |
| `IncludeExtensionsText` | string | TwoWay | 확장자 입력 TextBox(placeholder "exe,dll"). 콤마/세미콜론/공백 구분, 점 자동 보정, 빈 값=모든 파일. |

### 바인딩 가능 속성 (상태/읽기)
| 바인딩 이름 | 타입 | 용도 |
|---|---|---|
| `IsScanning` | bool | UI 잠금/취소버튼 가시성/진행률 영역 Visibility. |
| `ProgressFraction` | double (0~1) | `ProgressBar` Value (Minimum=0, Maximum=1). |
| `ProgressText` | string | 진행 상태 텍스트 "123 / 456 — Foo.dll". |
| `CurrentFile` | string? | 현재 처리 파일(부가 표시). |
| `StatusMessage` | string | 하단 상태표시줄 안내/결과 메시지. |
| `HasManifest` | bool | 검사 시작/매니페스트 열기 버튼 활성 표현(CanExecute가 이미 반영). |
| `HasResult` | bool (파생) | 요약/그리드 영역 Visibility. |
| `IsIntact` | bool (파생) | 요약 배지 색상(녹색=양호 / 적색=경고) 결정. |
| `OkCount` / `ModifiedCount` / `CorruptedCount` / `MissingCount` / `UnexpectedCount` | int (파생) | 요약 카운트 칩 5종. `Result` 변경 시 자동 알림. |
| `Result` | `IntegrityScanResult?` | 원본 결과(필요 시 직접 바인딩). |

### 컬렉션
| 바인딩 이름 | 타입 | 용도 |
|---|---|---|
| `Files` | `ObservableCollection<FileIntegrityItemViewModel>` | 결과 `DataGrid`/`ListView` `ItemsSource`. 문제 항목 우선 정렬됨. |

### 커맨드 (`[RelayCommand]` → `XxxCommand` 자동 생성)
| 바인딩 이름 | 종류 | CanExecute 조건 | 동작 |
|---|---|---|---|
| `ScanCommand` | async | `!IsScanning && HasManifest` | 옵션 빌드 → `VerifyAsync` → Files/요약 채움. |
| `GenerateBaselineCommand` | async | `!IsScanning` | `GenerateBaselineAsync` → 저장 → `HasManifest=true`. (덮어쓰기 확인 MessageBox는 View 측에서.) |
| `CancelCommand` | sync | `IsScanning` | 내부 `_cts.Cancel()`. **취소 버튼은 `IsScanning`일 때만 표시 권장.** |
| `BrowseTargetCommand` | sync (게이트) | `!IsScanning` | No-op 게이트. 실제 폴더 다이얼로그는 코드비하인드가 소유 → 선택 후 `vm.SetTargetDirectory(path)` 호출. |
| `OpenManifestLocationCommand` | sync (게이트) | `HasManifest` | No-op 게이트. 실제 탐색기 열기는 코드비하인드가 `vm.GetManifestPath()`로 경로 얻어 수행. |

> CanExecute 갱신은 `[NotifyCanExecuteChangedFor]`로 `IsScanning`/`HasManifest` 변경 시 자동.
> 별도 `OnIsScanningChanged`에서 `NotifyCanExecuteChanged` 수동 호출 불필요.

### View 코드비하인드가 호출하는 public 메서드 (MVVM 안전 분리)
| 메서드 | 시점 | 설명 |
|---|---|---|
| `Task InitializeAsync()` | Window `Loaded` | `await vm.InitializeAsync();` (매니페스트 상태 갱신). |
| `void SetTargetDirectory(string path)` | 폴더 다이얼로그 확정 후 | 대상 경로 설정 + 매니페스트 재평가. |
| `string GetManifestPath()` | 매니페스트 폴더 열기 직전 | 탐색기로 열 경로 획득(`Process.Start("explorer.exe", "/select,..")` 등). |

---

## FileIntegrityItemViewModel — 그리드 행 바인딩 표면

생성자: `FileIntegrityItemViewModel(FileIntegrityResult model)`. 표시 전용·읽기 전용 getter.

| 바인딩 이름 | 타입 | 용도 |
|---|---|---|
| `RelativePath` | string | 경로 컬럼. |
| `Status` | `IntegrityStatus` (enum) | 행 색상 `DataTrigger` 분기용 원본 값. |
| `StatusText` | string | 한글 상태 라벨: 정상/변조/손상/누락/추가. |
| `StatusBrushKey` | string | 상태→테마 브러시 **리소스 키 문자열**(아래 컨버터 요구 참조). |
| `ExpectedSha256Short` | string | 기대 해시 앞 12자. |
| `ActualSha256Short` | string | 실제 해시 앞 12자. |
| `SizeText` | string | 사람이 읽는 크기(예 "1.2 MB"). |
| `SignatureText` | string | 서명 상태 한글: 검사 안 함/유효/무효/서명 없음. |
| `Detail` | string | 오류/부가 설명(잠김/접근 거부 등). |
| `SortPriority` | int | (내부 정렬용, 바인딩 불필요) 문제 우선. VM이 이미 정렬해 추가함. |
| `Model` | `FileIntegrityResult` | 원본(필요 시). |

---

## UIDesigner를 위한 컨버터 요구사항

표현 속성을 VM에 미리 계산해 **컨버터를 최소화**했다. 그래도 다음 2종이 필요/권장:

1. **`BoolToVisibilityConverter`** (필수, 기존 존재 가능)
   - `IsScanning` → 진행률 영역 + 취소 버튼 Visibility.
   - `HasResult` → 요약/그리드 영역 Visibility.
   - 기존 `Views/Converters.cs`에 있으면 재사용, 없으면 추가.

2. **상태 색상 처리 (택1)**
   - **(권장 A) DataTrigger 분기**: `FileIntegrityItemViewModel.Status`(enum) 값으로 행/점 색상을 XAML `DataTrigger`에서 직접 분기. 컨버터 불필요.
   - **(B) StatusBrushKey 사용**: `StatusBrushKey`가 반환하는 리소스 키
     (`IntegrityOkBrush`, `IntegrityModifiedBrush`, `IntegrityCorruptedBrush`, `IntegrityMissingBrush`, `IntegrityUnexpectedBrush`)에 매핑되는 브러시를
     테마 리소스에 정의하고, `string → Brush` 룩업 컨버터(`StaticResource`/`FindResource`) 사용.
   - 어느 쪽이든 위 5개 상태 색상 의미: 정상=녹색, 변조=주황/적색, 손상=적색, 누락=회색/적, 추가=노랑/청 — 테마 팔레트에 맞게 UIDesigner 재량.

3. **요약 배지 색상**: `IsIntact`(bool)로 녹색("무결성 양호")/적색("경고") 분기 — `BoolToVisibility` 또는 `DataTrigger`로 처리(전용 컨버터 불필요).

> 권장: 행 색상은 (A) DataTrigger 방식. `StatusBrushKey`는 (B)를 택할 때만 쓰면 됨.

---

## 명세 준수 체크
- [x] `[ObservableProperty]`/`[RelayCommand]` 소스 생성기, `partial class : ObservableObject`.
- [x] 비동기 커맨드 `ScanCommand`/`GenerateBaselineCommand` + 내부 `_cts`(시작 시 생성, 완료/취소 시 dispose & null) + `CancelCommand`.
- [x] `IProgress<IntegrityProgress>`를 생성자(UI 스레드)에서 `Progress<T>`로 생성 → ProgressFraction/ProgressText/CurrentFile 마샬링.
- [x] 요약 카운트 5종(ok/modified/corrupted/missing/unexpected) `Result`에서 파생 + `[NotifyPropertyChangedFor]`.
- [x] `ObservableCollection<FileIntegrityItemViewModel>` Files, 문제 항목 우선 정렬.
- [x] `IsScanning` 기반 CanExecute(`[NotifyCanExecuteChangedFor]`).
- [x] 생성자 주입 `IBinaryIntegrityService`(DI 컨테이너 없음, App.xaml.cs 수동 주입).
- [x] `OperationCanceledException`→"검사 취소됨", `AgentException`/기타→"오류: {msg}".
- [x] MVVM 순수성: View 요소(Window/MessageBox/Dispatcher) 직접 참조 없음. 다이얼로그/탐색기는 View 코드비하인드 게이트 패턴.

## 후속(다른 에이전트) 참고
- App.xaml.cs 수동 등록(§7) 및 진입점은 Orchestrator/UIDesigner 담당.
- 빌드 검증은 미수행(병렬 작업 중). 서비스 구현 완료 후 통합 빌드 권장.
- 만약 ServiceEngineer 최종 시그니처가 §4와 달라지면(특히 `VerifyAsync`/`GenerateBaselineAsync`/`ManifestExists`/`GetDefaultTargetDirectory`/`GetManifestPath`) 호출부 조정 필요.
