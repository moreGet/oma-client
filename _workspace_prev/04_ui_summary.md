# 04 — View 레이어 구현 요약 (UIDesigner)

> Namespace: `OhMyAgent.AiAgent.Client.Views`. 다크 테마(Resources/Colors·Styles·Converters) 재사용.
> 빌드 상태: **`dotnet build` 오류 0개** (전체 솔루션 컴파일 성공).

## 생성/수정된 파일

### 생성 (CREATE)
- `Views/TranscriptItemTemplateSelector.cs` — `ITranscriptItem` 구체 타입(User/Assistant/System/ToolCall)별 DataTemplate 선택.
- `Resources/TranscriptTemplates.xaml` — 트랜스크립트 4종 DataTemplate + `TranscriptSelector` + 인라인 승인 카드 템플릿(`ApprovalCardTemplate`). MainWindow·ChatOnlyWindow 양쪽이 머지하여 재사용(중복 제거).

### 수정 (MODIFY)
- `MainWindow.xaml` / `MainWindow.xaml.cs` — d:DataContext + ctor `AgentSessionViewModel`. 기존 chat list → `Transcript` ItemsControl(셀렉터). 권한모드 ComboBox, 작업디렉토리/사용량 라벨, Send/Stop 버튼, 진행 ProgressBar, 인라인 승인 카드. 자동 스크롤은 `Transcript.CollectionChanged` 로 전환. 핫키 설정 메뉴를 `SettingsViewModel(settings, api)` 신규 ctor에 맞춤(+ `InitializeAsync`).
- `Views/ChatOnlyWindow.xaml` / `.xaml.cs` — d:DataContext + ctor `AgentSessionViewModel`. 컴팩트 `Transcript` 뷰 + 인라인 승인 카드 + Stop 버튼. always-on-top/no-taskbar/opacity/Esc-hide/OnClosing 동작 보존.
- `Views/SettingsWindow.xaml` / `.xaml.cs` — 작업디렉토리 폴더피커(`FolderBrowserDialog`→`SetWorkspaceRootAsync`), 권한모드 ComboBox(+Full-Auto 경고), MaxIterations/MaxTokens, ServerBaseUrl, AuthScheme ComboBox, AuthToken PasswordBox(바인딩 불가→코드비하인드로 `vm.AuthToken` 푸시 + 초기 시드), ModelId 편집형 ComboBox(+`LoadModelsCommand`), `SaveServerConfigCommand`. 기존 핫키 캡처 UI 보존(ScrollViewer로 감싸고 창 크기 480×660). 창은 NoResize 유지.
- `Views/Converters.cs` — 신규 컨버터: `NullToVisibilityConverter`(PendingApproval), `StringToVisibilityConverter`, `ToolRiskToBrushConverter`, `ToolRiskToTextConverter`, `ToolCallStatusToBrushConverter`, `ToolCallStatusToTextConverter`, `EnumEqualsConverter`.
- `Resources/Converters.xaml` — 위 컨버터 등록(키: NullToVisibility, StringToVisibility, ToolRiskToBrush, ToolRiskToText, ToolCallStatusToBrush, ToolCallStatusToText, EnumEquals).

## 주요 바인딩 경로 (모두 03 요약의 정확한 멤버명 사용)

### MainWindow / ChatOnlyWindow ← AgentSessionViewModel
| XAML 요소 | Binding | 멤버 |
|---|---|---|
| ItemsControl | `Transcript` | ObservableCollection<ITranscriptItem> |
| 입력 TextBox | `InputText` (TwoWay) | InputText |
| 전송 Button | `SendCommand` | SendCommand |
| 중지 Button (+Visibility) | `StopCommand`, `IsBusy` | StopCommand / IsBusy |
| ProgressBar Visibility | `IsBusy` | IsBusy |
| 연결 점 | `IsConnected` (BoolToStatusBrush) | IsConnected |
| 상태 텍스트 | `StatusText` | StatusText |
| 작업디렉토리 라벨 | `WorkspaceRoot` | WorkspaceRoot |
| 사용량 라벨 | `LastUsageText` (StringToVisibility) | LastUsageText |
| 권한 ComboBox | `PermissionModes` / `CurrentPermissionMode` (TwoWay) | PermissionModes / CurrentPermissionMode |
| 초기화 Button | `ClearCommand` | ClearCommand |
| 오류 패널 | `HasError` / `ErrorMessage` / `RetryConnectionCommand` | 동일 |
| 창 투명도 | `WindowOpacity` (TwoWay) | WindowOpacity |
| 인라인 승인 카드 | `PendingApproval` (NullToVisibility) | PendingApproval |

### Transcript item 템플릿
| 타입 | 바인딩 |
|---|---|
| UserTurnViewModel | `Text` |
| AssistantTurnViewModel | `Text`, `IsStreaming`(스트리밍 커서) |
| SystemNoticeViewModel | `Text` |
| ToolCallViewModel | `ToolName`, `Risk`(→brush/text), `Status`(→brush/text), `ArgsPreview`, `ResultText`, `IsError`, `IsExpanded`(TwoWay, 접이식 ToggleButton) |

### 승인 카드 ← ApprovalRequestViewModel (PendingApproval)
`ToolName`, `Risk`, `ArgsPreview` 표시 + `AllowCommand` / `DenyCommand` / `AlwaysAllowCommand`.

### SettingsWindow ← SettingsViewModel
`WorkspaceRoot`(읽기+폴더피커→`SetWorkspaceRootAsync`), `PermissionModes`/`PermissionMode`(TwoWay), `ShowFullAutoWarning`, `MaxIterations`, `MaxTokens`, `ServerBaseUrl`, `AuthSchemes`/`AuthScheme`, `AuthToken`(PasswordBox 코드비하인드), `AvailableModels`/`ModelId`, `LoadModelsCommand`, `SaveServerConfigCommand`. 기존: `DisplayText`/`IsCapturing`/`ValidationError`/`StartCaptureCommand`/`CancelCaptureCommand`/`SaveCommand`/`ApplyCapturedKey` 보존.

## 범위 밖이지만 빌드를 막던 선행 오류 2건 수정 (다른 레이어 — 보고용)
ServiceEngineer/ViewModelEngineer 산출물에 컴파일 오류가 남아 있어 **내 View 레이어 검증(빌드)이 불가**했음. `UseWPF`+`UseWindowsForms`+`ImplicitUsings`가 모두 켜진 조합에서만 드러나는 잠복 오류였고, 둘 다 1줄 최소 수정:
1. `Services/AgentOrchestrator.cs:216` — `catch` 절 안에서 `yield return` (CS1631 금지). → 실패 메시지를 플래그에 담고 catch 밖에서 yield 하도록 재구성.
2. `ViewModels/AgentSessionViewModel.cs:361` — 맨 `Application` 가 `System.Windows.Forms.Application` 과 `System.Windows.Application` 사이에서 모호(CS0104). → `System.Windows.Application.Current` 로 정규화.

> 두 파일 모두 git 미추적 신규 파일이며 내가 만든 게 아님(선행 산출물). QAReviewer/오케스트레이터가 소유 레이어 차원에서 재검토 권장.

## 가정 / 비고
1. **App.xaml 머지 사전은 그대로 둠.** `TranscriptTemplates.xaml` 은 각 Window의 `Window.Resources` 에서 머지(앱 전역 리소스만 참조하므로 안전).
2. **App.xaml.cs 에 `internal IAgentApiClient? Api` 1줄 추가** — MainWindow의 기존 "단축키 설정" 메뉴가 신규 `SettingsViewModel(settings, api)` ctor 를 호출하려면 api 접근자가 필요. 기존 `SettingsService` 접근자와 동일 패턴의 최소 노출.
3. PasswordBox 는 바인딩 불가 → `AuthTokenBox_PasswordChanged` 에서 `vm.AuthToken` 으로 푸시, ctor 에서 초기 시드.
4. 03 요약의 모든 멤버명을 그대로 바인딩 — 누락된 바인딩 타깃 없음. (MainWindow 구버전의 `Domains/SelectedDomain/IsMcpRunning/McpStatusText/ClearMessagesCommand` 는 신규 VM에 없어 제거/대체함.)
5. `Views/MessageTemplateSelector.cs` 와 `ChatMessageViewModel` 은 spec상 KEEP 이라 유지(현재 뷰에서 미사용·무해).
