# 05 — QA Report (데드코드 / 비효율 / 컨벤션 보수적 정리)

검증 범위: Services/(Tools 포함), Models/, ViewModels/(Transcript 포함), Views/(.cs + .xaml), Resources/*.xaml, App.xaml.cs, MainWindow.
방침: **명백·저위험**만 직접 적용. 대규모 리팩토링(DI 컨테이너 등)은 제안만. 동작 불변. README 미수정.

## 검증 결과: PASS — 빌드 경고 0 / 오류 0 (변경 전후 동일)

---

## 1. 직접 적용한 안전 개선

| # | 파일 | 변경 내용 | 근거 |
|---|------|----------|------|
| 1 | `Models/DomainOptions.cs` | **파일 전체 삭제** (`public record DomainOptions`) | 전 저장소 grep 결과 선언부 외 0참조. 완전한 데드 타입. |
| 2 | `App.xaml.cs` | 필드 `private IWorkspaceHistoryService? _workspaceHistory;` 선언 + 대입(`_workspaceHistory = workspaceHistory;`) 제거 | 대입만 되고 읽히지 않는 죽은 필드. 실제 사용은 지역변수 `workspaceHistory` 경유(라인 99·112·139). |
| 3 | `Views/Converters.cs` | `EnumEqualsConverter` 클래스 + 그 위 오배치된 잘못된 doc-comment 제거 | XAML/CS 어디에서도 `EnumEquals` 키 미사용(컨버터 0참조). |
| 4 | `Resources/Converters.xaml` | `<views:EnumEqualsConverter x:Key="EnumEquals"/>` 등록 라인 제거 | 위 3과 짝. 사용처 없음. |
| 5 | `Views/ChatOnlyWindow.xaml` | 미사용 `xmlns:views=...` 네임스페이스 선언 제거 | 파일 내 `views:` 접두사 0회 사용(`vm:`만 사용). |
| 6 | `ViewModels/AgentSessionViewModel.cs` | 미사용 `using System.Windows;` 제거 | 유일한 `System.Windows` 참조는 라인 707의 **완전수식** `System.Windows.Application.Current`. 비수식 타입 미사용. |
| 7 | `Services/SettingsService.cs` | 미사용 `using System.Windows;` 제거 | `Application` 은 라인 10 별칭(`using Application = System.Windows.Application;`)으로 해석됨. |
| 8 | `Services/Tools/ClipboardReadTool.cs` | 미사용 `using System.Windows;` 제거 | `Application`/`Clipboard` 모두 별칭(라인 7·8)으로 해석. |
| 9 | `Services/Tools/ClipboardWriteTool.cs` | 미사용 `using System.Windows;` 제거 | 위와 동일. |

> 위 9건 적용 후 재빌드 **경고 0 / 오류 0** 확인.

---

## 2. 코드로 재확인한 항목 (이상 없음 — 변경 없음)

### 2-1. STJ 마이그레이션 디스크 호환 (SettingsService.PersistenceOptions)
`SettingsService.PersistenceOptions` 가 기존 Newtonsoft 디스크 포맷을 정확히 재현함을 코드로 확인:
- `PropertyNamingPolicy = null` → **PascalCase 유지** (Newtonsoft 기본과 동일).
- enum 변환기 미등록 → `Modifiers`/`PermissionMode`/`KeyCode` **정수 직렬화** 유지(STJ·Newtonsoft 공통 기본).
- `WriteIndented = true`(2-space), `PropertyNameCaseInsensitive`, `ReadCommentHandling=Skip`, `AllowTrailingCommas` → 구파일 로드 견고성.
- 에이전트 와이어용 `AgentJson.Options`(camelCase 가능)와 **의도적으로 분리**되어 영속 포맷에 오염 없음.
- `WorkspaceHistoryEntry` 의 `[JsonPropertyName]` 이 PascalCase 로 교정되어 직렬화 정책과 무관하게 디스크 키 고정.
→ **기존 `%APPDATA%/OhMyAgent/settings.json` 무손실 호환. 문제 없음.**

### 2-2. 신규 도구 8종 ITool 계약 / ToolRisk 적정성
| 도구 | 부여 위험도 | 적정성 |
|------|-----------|--------|
| `get_environment` | ReadOnly | OK (조회) |
| `clipboard_read` | ReadOnly | OK |
| `clipboard_write` | Write | OK (상태 변경, 비파괴) |
| `list_processes` | ReadOnly | OK |
| `start_process` | **Execute** | OK — 명시 요구사항과 일치 |
| `kill_process` | **Destructive** | OK — 승인 게이트 대상, 명시 요구사항과 일치 |
| `http_fetch` | **Execute** | OK — 외부 부수효과, 명시 요구사항과 일치 |
| `screenshot` | ReadOnly | OK |
- 8종 모두 `ITool` 계약 충족, `public sealed class` 컨벤션 일치, `JsonElement ParametersSchema`/`Risk`/`ExecuteAsync` 정상 구현.
- App.xaml.cs `tools[]` 에 19개 정상 등록, `HttpFetchTool(_toolHttpClient)` 주입·`OnExit` Dispose 확인.
→ **계약/위험도 모두 적절. 변경 없음.**

### 2-3. 비동기/누수 패턴
- `.Result`/`.Wait()`/`Thread.Sleep` 부적절 사용 없음(검출된 `result.Result.Content` 는 Task 차단이 아닌 `Result` 명 프로퍼티).
- `async void` 2건 모두 정당: `App.OnStartup`(WPF 생명주기 override), `IntegrityWindow_Loaded`(이벤트 핸들러).
- 모든 ITool 은 stateless singleton 으로 이벤트 구독 없음 → 도구 측 누수 위험 없음.

---

## 3. 발견했으나 미적용한 리스크/품질 항목 (사용자 판단 필요)

| 파일 | 라인 | 유형 | 설명 | 권고 |
|------|------|------|------|------|
| `ViewModels/AgentSessionViewModel.cs` | 427 (`AttachFile`) + 419 (`CanAttachFile`) + 58 (NotifyCanExecuteChangedFor) | 사실상 데드 | `[RelayCommand]` 가 생성하는 `AttachFileCommand` 가 어떤 XAML 바인딩에서도 안 쓰임. 실제 "+" 버튼은 코드비하인드 `AttachButton_Click`(MainWindow.xaml.cs:40)로 동작. | 미적용. 제거하려면 커맨드+`Can*`+`NotifyCanExecuteChangedFor` 3곳을 함께 손봐야 하고, 향후 XAML 바인딩 전환 의도일 수 있어 **보고만**. |
| `ViewModels/IntegrityViewModel.cs` | 208 (`BrowseTarget`)·201(`CanBrowseTarget`)·42 / 220(`OpenManifestLocation`)·213(`CanOpenManifestLocation`)·52 | 사실상 데드 | 두 `[RelayCommand]` 모두 바인딩 미사용. 실제로 `BrowseTarget_Click`/`OpenManifestLocation_Click`(IntegrityWindow.xaml.cs) 코드비하인드 경유. doc-comment 상 "게이트 전용 no-op" 의도. | 미적용(동상). |
| `ViewModels/FileIntegrityItemViewModel.cs` | 43 (`StatusBrushKey`) | 데드 public 프로퍼티 | XAML/CS 0참조. UI 는 `Status` DataTrigger 사용. 자체 주석도 "대안-미채택" 명시. | 미적용. public 표면이라 보수적으로 보고만. 안전 제거 가능. |
| `ViewModels/IntegrityViewModel.cs` | 47 (`[ObservableProperty] _currentFile`) | write-only | 119·243·290 에서 대입만, 바인딩/읽기 없음. 진행 텍스트는 지역 `p.CurrentFile` 로 생성. | 미적용. observable public 표면이라 보고만. |
| `Services/Tools/*` 중 일부 | — | 컨벤션 비일관 | `ConfigureAwait(false)` 적용 도구(Read/Write/Edit/Grep/Run/HttpFetch)와 미적용 도구가 혼재. | 동작 무관 Low. 정책 단일화는 별도 작업으로 권고. |
| `Services/*` 서비스 구현 클래스 | — | 컨벤션 | ITool 구현은 전부 `sealed` 인데 다수 서비스 구현 클래스는 비-sealed `public class`. | DI 늦은 바인딩/상속 가능성 고려해 미적용. 봉인은 동작 불변이나 보고만. |

---

## 4. 아키텍처 개선 "제안" (적용 안 함 — 실무 권고)

1. **컴포지션 루트 분리 (App.xaml.cs 비대화)**
   `OnStartup` 이 16단계에 걸쳐 19개 도구 + 10여 개 서비스를 수동 `new` 한다. 경량 `Bootstrapper`(또는 `CompositionRoot`) 클래스로 와이어링을 추출하면 `App` 은 생명주기/트레이/핫키만 담당하게 되어 가독성·테스트성 향상. **DI 컨테이너 도입 없이도** 정적 팩토리 메서드 추출만으로 가능(저위험이나 범위가 커 이번엔 미적용).

2. **도구 등록 자동화**
   `tools[]` 배열에 신규 도구 추가 시 수기 등록이 필요(누락 위험). 리플렉션 기반 `ITool` 자동 수집(어셈블리 스캔 → 명시 팩토리) 또는 소스 제너레이터로 등록 자동화 권고. 단, 생성자 주입 도구(`HttpFetchTool`)·표시 순서 보장을 위해 명시 등록을 유지하는 절충안도 합리적.

3. **경량 DI 도입 후보**
   현재 수동 주입은 충분히 동작하나, 서비스/뷰모델 수가 더 늘면 `Microsoft.Extensions.DependencyInjection` 경량 컨테이너 도입을 고려. 단 WPF 윈도우/뷰모델 수명 관리와 STA 마샬링 경계를 정의한 후 진행해야 하므로 **별도 설계 작업**으로 분리 권고.

4. **PasswordBox ↔ ViewModel 동기화 일원화**
   로그인/인증토큰 PasswordBox 가 코드비하인드 `PasswordChanged` 푸시 + 생성자 시드의 혼합 패턴. 보안 제약상 불가피하나, 첨부 behavior 로 추출하면 코드비하인드 중복 제거 가능(권고, 동작 변경 수반하므로 미적용).

---

## 5. 최종 빌드 결과

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client.csproj
빌드했습니다.
    경고 0개
    오류 0개
```
**클린 빌드 유지(0/0). 기능/동작 변경 없음.**
