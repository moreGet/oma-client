# 09 — Service/Model 델타 구현 요약 (ServiceEngineer, Phase D)

스펙 `_workspace/08_delta_spec.md` 전부를 ServiceEngineer 소유 범위(Models/Services/AppSettings·migration/AgentSession/App.xaml.cs 와이어링/API_CONTRACT)에서 구현했다. **빌드 0 에러** (사전 존재 NU1510 NuGet 경고 2건만 잔존, 본 작업과 무관).

## 1. 신규 Models (CREATE)
- `Models/WorkspaceHistoryEntry.cs` — `Path`/`DisplayName`/`LastUsedUtc`. PascalCase 프로퍼티 + STJ 어트리뷰트(Newtonsoft·STJ 양립). settings.json에는 Newtonsoft 기본 PascalCase로 기록.
- `Models/ChatSessionRecord.cs` — `Id/Title/CreatedUtc/UpdatedUtc/WorkspaceRoot?/Messages[]`.
- `Models/ChatSessionSummary.cs` — positional record `(Id, Title, UpdatedUtc, WorkspaceRoot?, MessageCount)`.
- `Models/Attachment.cs` — `FilePath/FileName/SizeBytes/ContentType?`.
- `Models/Suggestion.cs` — positional record `(Text, Prompt?=null, Icon?=null)` + `[property: JsonPropertyName]`.

## 2. 수정 Models (MODIFY)
- `Models/Agent/AgentMessage.cs` — 예약 필드 `IReadOnlyList<Attachment>? Attachments` 추가(`[JsonPropertyName("attachments")]`). 팩토리 메서드 무변경. `AgentJson.Options`가 WhenWritingNull이므로 null 시 직렬화 생략 → **기존 요청 바이트 불변(서버 회귀 없음)**.
- `Models/AppSettings.cs` — `SchemaVersion` 기본값 3→4, 신규 `string UserDisplayName=""`, `List<WorkspaceHistoryEntry> RecentWorkspaces=[]`. `using System.Collections.Generic;` 추가.

## 3. 신규 Services (CREATE)
- `IWorkspaceHistoryService` / `WorkspaceHistoryService` — **실제**. ctor `(ISettingsService)`. `AddAsync`: `Path.TrimEndingDirectorySeparator(Path.GetFullPath())` 정규화 → OrdinalIgnoreCase 중복 제거 → 맨앞 삽입 → 상한 10 절단 → `settings.SaveAsync()` → `HistoryChanged` 발생. `TouchAsync`→`AddAsync` 위임(Add가 재삽입으로 최신화). `GetRecent`: LastUsedUtc desc 방어적 복사. **재진입 회피: `settings.Current.RecentWorkspaces` 직접 변경 + `SaveAsync`만 호출, `RaiseSettingsChanged`는 호출하지 않음** → App의 SettingsChanged→AddAsync 루프 차단.
- `IChatHistoryService` / `ChatHistoryService` — **실제**, `%APPDATA%/OhMyAgent/sessions/{id}.json`, `AgentJson.Options` 직렬화. `ListAsync`(디렉토리 스캔→Summary 투영, UpdatedUtc desc, 손상 파일 try/catch 스킵), `LoadAsync`(없으면 null), `SaveAsync`(`.tmp`→`File.Move(overwrite:true)` 원자적 upsert; 실패 시 AgentException), `DeleteAsync`(무해), `CreateNew`(Guid/"새 대화"/now/[]), `BuildTitle`(첫 user Content→개행 제거·공백 축약·40자 컷+"…", 없으면 "새 대화"). 모든 IO는 `Task.Run` + `_ioLock`. `PathFor`는 `Path.GetFileName(id)`로 경로 탈출 방지.
- `IFileAttachmentService` / `FileAttachmentService` — **클라 실제 + 전송 stub**. `CreateFromPath`(FileInfo로 메타 + 확장자→MIME 간이 매핑; 부재 시 AgentException). `ReadAsBase64Async`는 `NotImplementedException`(§8.2 미연결, 호출되지 않음).
- `ISuggestionService` / `StubSuggestionService` — **stub**. `GetSuggestionsAsync`→`Task.FromResult<IReadOnlyList<Suggestion>>([])`. §8.1 미래 연동 주석.

## 4. AppSettings/SettingsService 마이그레이션 (MODIFY)
- `ISettingsService.cs` — `UpdateUserDisplayNameAsync(string)`, `UpdateRecentWorkspacesAsync(IReadOnlyList<WorkspaceHistoryEntry>)` 추가. `using System.Collections.Generic;`.
- `SettingsService.cs`:
  - **누적형 마이그레이션 리팩토링 (CRITICAL fix)**: v3 블록의 `return true;` → `migrated = true;`로 변경(fall-through), v4 블록 이어서 실행, 마지막에 `return migrated;` 단일 반환. 이로써 신규 사용자(파일 있는 구버전 포함)가 v3·v4 두 블록을 모두 통과해 v4로 시드된다. v4 블록: `RecentWorkspaces ??= []; UserDisplayName ??= ""; SchemaVersion=4;`.
  - 신규 메서드 2개 구현(둘 다 `SaveAsync` + `RaiseSettingsChanged` 패턴). `using System.Linq;` 추가.

## 5. AgentSession (MODIFY)
- 기존 `Id` get-only 유지. 명시적 파라미터리스 ctor + 신규 복원 ctor `AgentSession(string id, IEnumerable<AgentMessage> messages)`(Id 주입 + Messages.AddRange) 추가. **이것이 유일한 변경**(기존 `new AgentSession()` 경로 보존).

## 6. App.xaml.cs 와이어링 (MODIFY)
- 신규 필드 `IWorkspaceHistoryService? _workspaceHistory`.
- 생성 순서: 8b) `WorkspaceHistoryService(_settingsService)` → 9b) `ChatHistoryService()` / `FileAttachmentService()` / `StubSuggestionService()`.
- 루트 VM 생성을 **신규 9-파라미터 시그니처**로 호출(§4.1, ViewModelEngineer 구현과 일치 확인).
- §15 SettingsChanged 핸들러에 워크스페이스 자동 Add 부착: `if (!IsNullOrWhiteSpace(s.WorkspaceRoot)) _ = workspaceHistory.AddAsync(s.WorkspaceRoot);` — **AddAsync가 RaiseSettingsChanged를 호출하지 않으므로 재진입 루프 없음**.

## 7. docs/API_CONTRACT.md (MODIFY)
- §7 뒤, 최종 `---` 앞에 **신규 §8** 삽입: 8.1 GET /suggestions, 8.2 첨부 transport(messages[].attachments[] 예약), 8.3 세션 동기화 엔드포인트 초안(GET/PUT/DELETE), 8.4 서버팀 결정 요청. 인터페이스/DTO/엔드포인트 초안만.

---

## 와이어링한 정확한 VM 생성자 시그니처 (App.xaml.cs가 호출)
```csharp
public AgentSessionViewModel(
    IAgentOrchestrator orchestrator,
    IAgentApiClient api,
    IPermissionService permissions,
    IWorkspaceContext workspace,
    ISettingsService settings,
    IWorkspaceHistoryService workspaceHistory,   // 신규 (B)
    IChatHistoryService chatHistory,             // 신규 (C)
    IFileAttachmentService attachments,          // 신규 (D)
    ISuggestionService suggestions);             // 신규 (G)
```
ViewModelEngineer가 병렬 구현한 실제 ctor와 **정확히 일치**(라인 104–113) 확인 → 전체 빌드 0 에러.

## 가정 / 결정 사항
- **WorkspaceHistoryService 영속 경로**: 스펙 §6의 명시 요구에 따라 `UpdateRecentWorkspacesAsync`(RaiseSettingsChanged 호출) 대신 `settings.Current.RecentWorkspaces` 직접 변경 + `settings.SaveAsync()` 경로를 채택해 SettingsChanged 루프를 회피했다. `ISettingsService.UpdateRecentWorkspacesAsync`는 스펙대로 인터페이스에 추가·구현했으나(일관성/타 호출자 대비) WorkspaceHistoryService 내부에서는 사용하지 않는다.
- **TouchAsync = AddAsync**: 스펙상 "있으면 LastUsedUtc만 갱신, 없으면 Add". Add가 기존 항목 제거 후 now로 재삽입하므로 의미가 동일 → AddAsync로 위임.
- **ReadAsBase64Async**: 스펙 §2.3가 "NotImplementedException 또는 Task.FromResult("") — 재량, 호출되지 않음"을 허용. 미연결 경로를 명확히 드러내기 위해 `NotImplementedException`을 선택.
- **MIME 매핑**: 간이 switch(txt/json/pdf/png/jpg/zip/cs/js/py 등). 미지정 확장자는 null(스펙 허용).
- **ChatHistoryService.PathFor**: id에 `Path.GetFileName` 적용해 경로 탈출 방지(스펙 미명시였으나 안전상 추가).
- `AgentMessage.Attachments` null-omit는 `AgentJson.Options.DefaultIgnoreCondition = WhenWritingNull`로 보장됨(AgentJson.cs 확인).
- App의 `_workspaceHistory` 필드는 현재 단일 사용이나 향후 OnExit 세션 저장/재참조 대비 보관(스펙 §6 "선택" 항목).

## 미구현(스펙 §11 제외 범위 — 의도적)
첨부 실제 서버 전송, 제안 실제 조회, 채팅 히스토리 서버 동기화. 모두 §8 계약 경계까지만.
