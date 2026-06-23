# 04. UI 레이어 구현 요약 — 바이너리 무결성 검사 화면

> 작성자: UIDesigner
> 담당: 명세 §6 / §11 `IntegrityWindow.xaml(.cs)` + 상태 브러시 리소스
> 바인딩 계약 기준: `_workspace/03_viewmodel_summary.md` (IntegrityViewModel / FileIntegrityItemViewModel)

---

## 생성 / 수정 파일

| 파일 | 구분 | 내용 |
|------|------|------|
| `OhMyAgent.AiAgent.Client/Views/IntegrityWindow.xaml` | 신규 | 무결성 검사 독립 윈도우. SettingsWindow 테마 차용(WindowStyle=None, AllowsTransparency, 공유 StaticResource). |
| `OhMyAgent.AiAgent.Client/Views/IntegrityWindow.xaml.cs` | 신규 | 코드비하인드(타이틀바 드래그/닫기/Loaded·다이얼로그·셸 위임). |
| `OhMyAgent.AiAgent.Client/Resources/Colors.xaml` | 수정 | 무결성 상태 브러시 5종 추가(아래 참조). |

> `Views/Converters.cs` 및 `Resources/Converters.xaml`은 **수정하지 않음** — 필요한 컨버터(`BoolToVisibility`, `InverseBool`)가 이미 존재. 신규 컨버터 불필요.

---

## 추가한 리소스 키 (Colors.xaml)

상태 색상은 권장안 **(A) DataTrigger 방식**으로 행 표시를 구현했고, 동시에
요약 칩/상태 점에서도 재사용할 수 있도록 5종 브러시를 팔레트 기존 색에서 정의했다.
이 키들은 `FileIntegrityItemViewModel.StatusBrushKey`가 반환하는 문자열과 **정확히 일치**하므로
추후 (B) StatusBrushKey 룩업 방식으로 전환해도 그대로 호환된다.

| 리소스 키 | 색상 | 의미 |
|---|---|---|
| `IntegrityOkBrush`         | `#34D399` (녹색) | 정상 |
| `IntegrityModifiedBrush`   | `#FBBF24` (주황) | 변조 |
| `IntegrityCorruptedBrush`  | `#FB7185` (적색) | 손상 |
| `IntegrityMissingBrush`    | `#9CA3B4` (회색) | 누락 |
| `IntegrityUnexpectedBrush` | `#60A5FA` (청색) | 추가 |

> 모두 기존 팔레트(상태색/텍스트색) 톤과 일치. 신규 색 임의 도입 최소화.

---

## 사용한 바인딩 / 커맨드 (모두 03 요약 계약과 일치)

### 입력/옵션 (양방향)
| XAML 요소 | Binding | 모드 |
|---|---|---|
| 대상 경로 TextBox | `TargetDirectory` | OneWay (ReadOnly) |
| 재귀 CheckBox | `RecursiveOption` | TwoWay |
| 서명 검사 CheckBox | `VerifySignaturesOption` | TwoWay |
| 확장자 TextBox | `IncludeExtensionsText` | TwoWay (PropertyChanged) |

### 상태/진행률 (읽기)
| XAML 요소 | Binding | 컨버터/용도 |
|---|---|---|
| 옵션 카드 IsEnabled | `IsScanning` | `InverseBool` (스캔 중 입력 잠금) |
| 진행률 영역 Visibility | `IsScanning` | `BoolToVisibility` |
| 취소 버튼 Visibility | `IsScanning` | `BoolToVisibility` (스캔 중에만 표시) |
| ProgressBar Value | `ProgressFraction` | Min=0 Max=1 |
| 진행 텍스트 | `ProgressText` | — |
| 요약 영역 Visibility | `HasResult` | `BoolToVisibility` |
| 카운트 칩 | `OkCount`/`ModifiedCount`/`CorruptedCount`/`MissingCount`/`UnexpectedCount` | — |
| 종합 배지 색/텍스트 | `IsIntact` | DataTrigger (양호=녹색/경고=적색) |
| 매니페스트 열기 IsEnabled | `HasManifest` | — |
| 상태표시줄 | `StatusMessage` | — |

### 컬렉션 / 결과 그리드 (DataGrid, `ItemsSource={Binding Files}`)
- 행 가상화(`EnableRowVirtualization`/`EnableColumnVirtualization`) — 대용량 대응.
- 컬럼: **상태**(색상 점 + `StatusText`), **상대 경로**(`RelativePath`), **기대 해시**(`ExpectedSha256Short`), **실제 해시**(`ActualSha256Short`), **크기**(`SizeText`), **서명**(`SignatureText`), **비고**(`Detail`).
- 상태 점 색상: `FileIntegrityItemViewModel.Status`(enum) 값으로 `DataTrigger` 분기 — `{x:Static models:IntegrityStatus.Xxx}` 매칭. (`models:` = `clr-namespace:OhMyAgent.AiAgent.Client.Models`.)

### 커맨드 / 코드비하인드 위임
| 동작 | 처리 방식 |
|---|---|
| 검사 시작 | `Command={Binding ScanCommand}` (직접 바인딩) |
| 취소 | `Command={Binding CancelCommand}` |
| 기준 생성 | 버튼 `Click` → 코드비하인드에서 덮어쓰기 확인 MessageBox 후 `GenerateBaselineCommand.Execute` 호출 |
| 찾아보기 | 버튼 `Click` → `FolderBrowserDialog` → `vm.SetTargetDirectory(path)` |
| 매니페스트 열기 | 버튼 `Click` → `vm.GetManifestPath()` → `explorer.exe /select` |

> `BrowseTargetCommand`/`OpenManifestLocationCommand`(게이트 no-op)는 바인딩하지 않고, 03 요약 권장대로 코드비하인드가 다이얼로그/셸을 소유하고 VM public 메서드에 위임한다.

---

## 코드비하인드 의존성 (생성자 시그니처)

```csharp
public IntegrityWindow(IntegrityViewModel vm)
```

- `Loaded` 이벤트에서 `await vm.InitializeAsync()` 호출(매니페스트 상태 갱신).
- `System.Windows.Forms.FolderBrowserDialog` 사용 — 프로젝트 `UseWindowsForms=true` 확인됨.
- `Process.Start("explorer.exe", ...)`로 매니페스트 폴더 열기(예외는 `Debug.WriteLine` 흡수).

---

## App.xaml.cs 진입점 안내 (Orchestrator용)

명세 §7 패턴. 서비스 수동 주입 후 윈도우를 띄운다:

```csharp
IAuthenticodeVerifier? authenticode = new AuthenticodeVerifier(); // 선택, null 가능
var binaryIntegrity = new BinaryIntegrityService(authenticode);

// 트레이 메뉴 / 설정 버튼 핸들러에서:
var vm  = new IntegrityViewModel(binaryIntegrity); // ⚠ UI 스레드에서 생성(Progress<T> 마샬링)
var win = new IntegrityWindow(vm);
win.Show();
```

- `IntegrityViewModel`은 **UI 스레드에서 생성** 필수(생성자에서 `Progress<T>` 캡처).
- `App.xaml` MergedDictionaries는 이미 Colors/Styles/Converters를 병합하므로 추가 등록 불필요.
- `IntegrityWindow.Owner` 설정은 진입점 재량(모달 아님, `Show()`).

---

## 명세/계약 준수 체크
- [x] SettingsWindow 테마 차용(WindowStyle=None + AllowsTransparency + 공유 StaticResource: WindowBg/SurfaceBg/BorderBrush/AccentGradient/TextPrimary 등).
- [x] 기존 스타일 재사용(`FloatingCard`, `PrimaryButton`, `OutlineButton`, `CaptionButton`, `DarkTextBox`, `CheckBox`, `Chip`). 인라인 하드코딩 색 미도입(상태 브러시만 팔레트에 추가).
- [x] 모든 바인딩 경로를 IntegrityViewModel / FileIntegrityItemViewModel 실제 멤버와 대조 검증.
- [x] DataContext 생성자 주입(SettingsWindow 패턴), `d:DataContext` 디자인 인스턴스 설정.
- [x] 컨버터 중복 정의 없음(기존 `BoolToVisibility`/`InverseBool` 재사용).
- [x] 코드비하인드 최소화 — 프레임워크 이벤트(Loaded/드래그/Click) + 다이얼로그/셸 위임만.
- [x] DataGrid 행/열 가상화로 대용량(엣지 #9) 대응.
- [x] 빌드 검증 미수행(지시 사항).

## 후속 참고
- 빌드는 미수행. 통합 빌드 시 `models:` x:Static 참조(`IntegrityStatus`)가 `OhMyAgent.AiAgent.Client.Models` 네임스페이스로 해석되는지 확인(현재 enum 위치 확인 완료).
- 진입점(트레이 메뉴 vs 설정 버튼) 최종 선택과 App.xaml.cs 등록은 Orchestrator 담당.
