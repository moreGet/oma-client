# 10 — ViewModel 델타 구현 요약 (ViewModelEngineer, Phase D)

스펙 `_workspace/08_delta_spec.md` §4 구현 완료. 본 문서는 UIDesigner 바인딩용 최종 멤버 목록.

대상 파일:
- `OhMyAgent.AiAgent.Client/ViewModels/AgentSessionViewModel.cs` (확장)
- `OhMyAgent.AiAgent.Client/ViewModels/SettingsViewModel.cs` (확장)

---

## AgentSessionViewModel

### 최종 생성자 시그니처 (App.xaml.cs가 그대로 주입 — 확정)
```csharp
public AgentSessionViewModel(
    IAgentOrchestrator orchestrator,
    IAgentApiClient api,
    IPermissionService permissions,
    IWorkspaceContext workspace,
    ISettingsService settings,
    IWorkspaceHistoryService workspaceHistory,
    IChatHistoryService chatHistory,
    IFileAttachmentService attachments,
    ISuggestionService suggestions)
```
스펙 §4.1 시그니처와 **바이트 단위 일치**.

### 신규 바인딩 가능 프로퍼티 (Observable)
| 프로퍼티 | 타입 | 용도 |
|---|---|---|
| `GreetingText` | `string` | F — "{이름}님 안녕하세요, 어떤 업무를 시작할까요?". 환영 헤딩에 바인딩. |
| `UserDisplayName` | `string` | F — 원시 이름(표시용). 헤딩은 `GreetingText` 권장. |
| `HasAttachments` | `bool` | D — `Attachments.Count > 0` 미러(컬렉션 변경 시 자동 갱신). UI는 `Attachments.Count` + `CountToVisibility`로 대체 가능. |

### 신규 컬렉션 (ObservableCollection)
| 컬렉션 | 타입 | 용도 |
|---|---|---|
| `RecentWorkspaces` | `ObservableCollection<WorkspaceHistoryEntry>` | B — 사이드바 "프로젝트" 목록. 항목: `.Path`, `.DisplayName`, `.LastUsedUtc`. |
| `ChatSessions` | `ObservableCollection<ChatSessionSummary>` | C — 사이드바 "채팅" 목록. 항목: `.Id`, `.Title`, `.UpdatedUtc`, `.WorkspaceRoot`, `.MessageCount`. |
| `Attachments` | `ObservableCollection<Attachment>` | D — 컴포저 첨부 칩. 항목: `.FilePath`, `.FileName`, `.SizeBytes`, `.ContentType`. |
| `Suggestions` | `ObservableCollection<Suggestion>` | G — 환영 제안(현재 빈 목록, UI는 비면 숨김). 항목: `.Text`, `.Prompt`, `.Icon`. |

### 신규 커맨드
| 커맨드 (바인딩명) | 타입 | CommandParameter | CanExecute |
|---|---|---|---|
| `OpenWorkspaceCommand` | `AsyncRelayCommand<WorkspaceHistoryEntry>` | `{Binding}` (항목) | param!=null && !IsBusy |
| `RemoveWorkspaceCommand` | `AsyncRelayCommand<WorkspaceHistoryEntry>` | `{Binding}` (항목) | param!=null |
| `LoadChatSessionCommand` | `AsyncRelayCommand<ChatSessionSummary>` | `{Binding}` (항목) | param!=null && !IsBusy |
| `DeleteChatSessionCommand` | `AsyncRelayCommand<ChatSessionSummary>` | `{Binding}` (항목) | param!=null |
| `AttachFileCommand` | `RelayCommand` | — | !IsBusy (버튼 IsEnabled 게이트용; 실제 다이얼로그는 코드비하인드) |
| `RemoveAttachmentCommand` | `RelayCommand<Attachment>` | `{Binding}` (항목) | param!=null |

> ItemsControl 내부에서 부모 VM 커맨드 바인딩 시:
> `Command="{Binding DataContext.OpenWorkspaceCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}" CommandParameter="{Binding}"`

### 기존 커맨드 변경 (바인딩명 유지)
- `ClearCommand` : `RelayCommand` → **`AsyncRelayCommand`로 승격**. 바인딩명 `ClearCommand` 그대로(메서드 `ClearAsync`, 생성기가 `Async` 접미 제거). UIDesigner **무변경**. 동작: 현재 세션 저장 → 초기화 → 채팅목록 갱신.
- `SendCommand` : 시그니처 무변경. 전송 시 `Attachments.Clear()` 호출 + 턴 종료 후 세션 자동 저장.

### 첨부 다이얼로그 패턴 (D — MVVM 안전, 코드비하인드 연동)
- VM은 `OpenFileDialog`를 열지 않는다.
- VM 공개 진입점: `public void AddAttachmentPublic(string path)` — MainWindow 코드비하인드가 파일 선택 후 경로마다 호출.
- `+` 버튼: `Click="AttachButton_Click"`(코드비하인드 OpenFileDialog, Multiselect) + `IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBool}}"`.
- 코드비하인드 호출 예: `(DataContext as AgentSessionViewModel)?.AddAttachmentPublic(path);`

### 비고
- `RecentWorkspaces`는 `InitializeAsync` 및 `IWorkspaceHistoryService.HistoryChanged`(Dispatcher 마샬링)로 자동 갱신.
- `ChatSessions`는 `InitializeAsync`/턴완료/Clear/Load/Delete 시 갱신.
- `Suggestions`는 현재 stub(빈 목록) → UI는 `Suggestions.Count` + `CountToVisibility`로 숨김.
- 트랜스크립트 복원: User/Assistant(텍스트)/Tool(결과 카드)/ToolCall 카드 매핑. System 메시지는 미표시.

---

## SettingsViewModel

생성자/기존 멤버 무변경. 신규 1프로퍼티 + 1커맨드(둘 다 선택적 UI):

| 멤버 | 타입 | 용도 |
|---|---|---|
| `UserDisplayName` | `string` (ObservableProperty) | F — "사용자 이름" 입력 바인딩. ctor에서 `c.UserDisplayName` 시드. |
| `SaveUserProfileCommand` | `AsyncRelayCommand` | F — `ISettingsService.UpdateUserDisplayNameAsync(UserDisplayName)` 호출. |

> 설정창 "사용자 이름" 입력 + 저장 버튼은 **선택 사항**. 미구현 시에도 빌드 영향 없음.

---

## 빌드 상태
- ViewModels/ 산출물: **오류 0, 경고 0** (빌드 진단상 ViewModels 귀속 항목 없음).
- 전체 빌드 잔여 오류 2건은 `Services/SettingsService.cs`(ServiceEngineer 영역: `UpdateUserDisplayNameAsync`/`UpdateRecentWorkspacesAsync` 미구현) — 병렬 작업 완료 시 해소 예정. ViewModel 측 의존(인터페이스·모델·AgentSession ctor·AppSettings 필드)은 모두 존재 확인됨.
