# 08 — 사이드바 실기능 델타 설계 (Architect, Phase D)

> 직전 Phase(07 Codex 레이아웃)의 정적 플레이스홀더를 실기능으로 교체하는 **델타 빌드 스펙**.
> 네임스페이스 규칙: `OhMyAgent.AiAgent.Client.{Models|Services|ViewModels|Views|Converters}`.
> 직렬화는 기존 `OhMyAgent.AiAgent.Client.Services.AgentJson.Options`(snake_case enum, null 무시) 재사용.
> ServiceEngineer / ViewModelEngineer / UIDesigner가 **병렬·무모호** 구현 가능하도록 모든 시그니처 확정.
>
> 요구사항 매핑: A(네비정리)·B(워크스페이스 히스토리)·C(채팅 히스토리)·D(첨부)·E(마이크삭제)·F(인사멘트)·G(제안 인터페이스)·H(#7 서버 계약).

---

## 0. 핵심 설계 결정 (구현 전 합의)

1. **세션 캡처 전략 (C):** `AgentSession`(Id/Messages/LastUsage)을 그대로 `ChatSessionRecord`로 매핑한다.
   `AgentSessionViewModel._session.Messages`가 곧 진실의 원천(orchestrator가 in-place로 append). VM은 *추가 상태를 만들지 않고* 이 리스트를 직렬화/복원한다.
   - **저장 시점:** `AgentDone` 수신 시(턴 완료) + `Clear`(새 채팅) 직전 + 앱 종료 시. delta-free하게 "현재 세션을 디스크에 upsert"만 한다.
   - **복원 시점:** 사이드바 채팅 항목 클릭 → record 로드 → `_session`을 messages로 재구성 → `Transcript`를 messages로부터 재투영.
   - 기존 orchestrator/AgentSession **무변경**. 단, VM이 `_session`을 교체할 수 있도록 내부 메서드만 추가(아래 4.1).

2. **워크스페이스 히스토리 = settings 영속 (B):** 별도 파일 대신 `AppSettings.RecentWorkspaces`에 보관(작고, settings 변경 이벤트에 자연 연동). 서비스는 settings를 래핑.

3. **채팅 히스토리 = 개별 JSON 파일 (C):** `%APPDATA%/OhMyAgent/sessions/{id}.json`. 목록 조회는 디렉토리 스캔 + 헤더만 읽어 `ChatSessionSummary` 반환(전체 메시지 로드는 클릭 시).

4. **첨부 (D):** 클라이언트는 실제 파일 선택/메타 읽기까지 구현. 서버 전송은 `AgentMessage.Attachments` **예약 필드**(직렬화되나 서버 미소비) + API_CONTRACT 문서화까지만. 실제 base64 페이로드 빌드는 stub(주석)로 남김.

5. **제안 (G):** `ISuggestionService`는 **stub**(빈 목록). VM `Suggestions` 컬렉션은 비어 있고 UI는 `CountToVisibility`로 숨김. 서버 엔드포인트는 API_CONTRACT 초안만.

6. **스키마 버전:** `AppSettings.SchemaVersion` 3 → **4** bump. 마이그레이션은 `RecentWorkspaces`·`UserDisplayName` 기본값 시드.

---

## 1. 신규/수정 Models

### 1.1 신규 — `Models/WorkspaceHistoryEntry.cs` (B)
```csharp
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>최근 사용한 작업 디렉토리 1건. AppSettings.RecentWorkspaces에 영속.</summary>
public sealed record WorkspaceHistoryEntry
{
    [JsonPropertyName("path")]         public required string Path { get; init; }
    [JsonPropertyName("display_name")] public required string DisplayName { get; init; }  // 보통 Path.GetFileName(Path)
    [JsonPropertyName("last_used_utc")]public DateTimeOffset LastUsedUtc { get; init; }
}
```
- `JsonPropertyName`은 `System.Text.Json.Serialization`. (AppSettings는 Newtonsoft로 직렬화되나, 이 record는 그 하위로 함께 직렬화되므로 **속성명은 Newtonsoft·STJ 양쪽에서 동작하도록** PascalCase 프로퍼티 + STJ 어트리뷰트로 둔다. Newtonsoft는 기본적으로 PascalCase로 직렬화하며 STJ 어트리뷰트를 무시 → 양립. ServiceEngineer는 settings.json에 `Path/DisplayName/LastUsedUtc` PascalCase로 나오는 것을 기대.)

### 1.2 신규 — `Models/ChatSessionRecord.cs` (C)
```csharp
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>디스크에 영속되는 한 대화 세션 전체. sessions/{Id}.json. 직렬화: AgentJson.Options.</summary>
public sealed record ChatSessionRecord
{
    [JsonPropertyName("id")]             public required string Id { get; init; }
    [JsonPropertyName("title")]          public required string Title { get; init; }
    [JsonPropertyName("created_utc")]    public DateTimeOffset CreatedUtc { get; init; }
    [JsonPropertyName("updated_utc")]    public DateTimeOffset UpdatedUtc { get; init; }
    [JsonPropertyName("workspace_root")] public string? WorkspaceRoot { get; init; }
    [JsonPropertyName("messages")]       public IReadOnlyList<AgentMessage> Messages { get; init; } = [];
}
```

### 1.3 신규 — `Models/ChatSessionSummary.cs` (C, 목록 경량 DTO)
```csharp
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>사이드바 채팅 목록용 헤더 정보(메시지 본문 제외).</summary>
public sealed record ChatSessionSummary(
    string Id,
    string Title,
    DateTimeOffset UpdatedUtc,
    string? WorkspaceRoot,
    int MessageCount);
```

### 1.4 신규 — `Models/Attachment.cs` (D)
```csharp
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>컴포저에 첨부된 로컬 파일 1건(클라이언트 메타).</summary>
public sealed record Attachment
{
    [JsonPropertyName("file_path")]   public required string FilePath { get; init; }   // 로컬 절대경로 (전송 시 제외 가능)
    [JsonPropertyName("file_name")]   public required string FileName { get; init; }
    [JsonPropertyName("size_bytes")]  public long SizeBytes { get; init; }
    [JsonPropertyName("content_type")]public string? ContentType { get; init; }        // MIME 추정, 미지정 가능
}
```

### 1.5 신규 — `Models/Suggestion.cs` (G)
```csharp
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>서버가 제공할 동작 힌트 1건(현재 stub로 빈 목록).</summary>
public sealed record Suggestion(
    [property: JsonPropertyName("text")]   string Text,           // 표시 문구 ("~~ 해보세요")
    [property: JsonPropertyName("prompt")] string? Prompt = null, // 클릭 시 InputText에 채울 실제 프롬프트
    [property: JsonPropertyName("icon")]   string? Icon = null);  // Segoe Fluent 글리프(선택)
```

### 1.6 수정 — `Models/Agent/AgentMessage.cs` (D #7 예약 필드)
- 기존 record에 **속성 1개 추가**(나머지 무변경, 팩토리 메서드 무변경):
```csharp
/// <summary>예약 필드 — 클라이언트 첨부 메타. 서버 소비는 미래(§8 API_CONTRACT). null이면 직렬화 생략.</summary>
[JsonPropertyName("attachments")]
public IReadOnlyList<Attachment>? Attachments { get; init; }
```
- `AgentJson.Options`가 `WhenWritingNull`이므로 기존 요청은 바이트 단위로 불변(첨부 없으면 필드 미출력). **서버 호환 안전.**

### 1.7 수정 — `Models/AppSettings.cs` (B, F)
- 기존 필드 전부 유지. **2개 추가 + SchemaVersion 4**:
```csharp
public int SchemaVersion { get; set; } = 4;   // 3 -> 4

// 신규 (Phase D)
public string UserDisplayName { get; set; } = "";                       // empty => Environment.UserName fallback (F)
public List<WorkspaceHistoryEntry> RecentWorkspaces { get; set; } = []; // 최근순, 상한 10 (B)
```

---

## 2. 신규 Services (인터페이스 + 구현 책임 + 저장 경로)

> 공통: `using OhMyAgent.AiAgent.Client.Models;`, `CancellationToken ct = default` 마지막 파라미터, 실패는 `AgentException` 또는 안전 폴백.

### 2.1 `IWorkspaceHistoryService` / `WorkspaceHistoryService` — 실제 구현 (B)
파일: `Services/IWorkspaceHistoryService.cs`, `Services/WorkspaceHistoryService.cs`
저장: `AppSettings.RecentWorkspaces`(settings.json 내, settings 서비스 경유). 별도 파일 없음.
```csharp
public interface IWorkspaceHistoryService
{
    /// <summary>최근순 정렬된 스냅샷(상한 10).</summary>
    IReadOnlyList<WorkspaceHistoryEntry> GetRecent();

    /// <summary>경로 추가/갱신: 정규화→대소문자 무시 중복 제거→LastUsedUtc=now→최근순 정렬→상한 10→settings 저장. 변경 알림 발생.</summary>
    Task AddAsync(string path, CancellationToken ct = default);

    /// <summary>해당 경로 제거 후 저장.</summary>
    Task RemoveAsync(string path, CancellationToken ct = default);

    /// <summary>이미 존재하는 경로의 LastUsedUtc만 갱신(없으면 Add와 동일).</summary>
    Task TouchAsync(string path, CancellationToken ct = default);

    /// <summary>목록 변경 시 발생(VM이 구독해 컬렉션 갱신).</summary>
    event EventHandler? HistoryChanged;
}
```
**구현 책임 (실제):**
- 생성자 `WorkspaceHistoryService(ISettingsService settings)`.
- `AddAsync`: `path` → `Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))` 정규화. `DisplayName = Path.GetFileName(정규화경로)`(빈 경우 정규화경로 자체). `StringComparison.OrdinalIgnoreCase`로 기존 제거 후 맨 앞 삽입. 11개 초과 시 꼬리 절단. `settings.Current.RecentWorkspaces`에 대입 후 `settings.SaveAsync()`. 이후 `HistoryChanged?.Invoke`.
- `GetRecent`: `settings.Current.RecentWorkspaces`를 `LastUsedUtc desc`로 정렬해 반환(방어적 복사).
- 스레드: settings 서비스의 IO락에 위임. UI 알림은 VM이 Dispatcher 처리.

### 2.2 `IChatHistoryService` / `ChatHistoryService` — 실제 구현 (C)
파일: `Services/IChatHistoryService.cs`, `Services/ChatHistoryService.cs`
저장: `%APPDATA%/OhMyAgent/sessions/{id}.json` (파일당 1세션, `AgentJson.Options`로 직렬화).
```csharp
public interface IChatHistoryService
{
    /// <summary>sessions 디렉토리를 스캔해 요약 목록을 UpdatedUtc desc로 반환. 손상 파일은 건너뜀.</summary>
    Task<IReadOnlyList<ChatSessionSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>id의 전체 record 로드. 없으면 null.</summary>
    Task<ChatSessionRecord?> LoadAsync(string id, CancellationToken ct = default);

    /// <summary>record를 {id}.json에 원자적 upsert(임시파일→교체). UpdatedUtc는 호출자가 세팅.</summary>
    Task SaveAsync(ChatSessionRecord record, CancellationToken ct = default);

    /// <summary>{id}.json 삭제(없어도 무해).</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>새 빈 record 생성(Id=Guid, Title="새 대화", Created=Updated=now, Messages=[]). 디스크 미기록.</summary>
    ChatSessionRecord CreateNew(string? workspaceRoot = null);

    /// <summary>메시지 목록으로부터 Title 생성: 첫 user 메시지 Content를 1줄·앞 40자로 요약(없으면 "새 대화").</summary>
    string BuildTitle(IReadOnlyList<AgentMessage> messages);
}
```
**구현 책임 (실제):**
- 디렉토리 상수: `Path.Combine(SpecialFolder.ApplicationData, "OhMyAgent", "sessions")`. 없으면 `SaveAsync` 시 생성.
- `ListAsync`: `Directory.EnumerateFiles(dir, "*.json")` → 각 파일 역직렬화하여 `ChatSessionSummary(Id, Title, UpdatedUtc, WorkspaceRoot, Messages.Count)` 투영. (대형 최적화는 후순위 — MVP는 전체 파싱 허용.) 손상 파일 try/catch 스킵.
- `SaveAsync`: 임시 `.tmp` 작성 후 `File.Move(overwrite:true)`로 교체(부분쓰기 방지). IO락.
- `BuildTitle`: `messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Content` → 개행 제거·Trim·40자 컷(초과 시 "…").
- 모든 IO는 `Task.Run`/비동기 파일 API로 백그라운드 수행.

### 2.3 `IFileAttachmentService` / `FileAttachmentService` — 클라이언트 실제 구현 (D)
파일: `Services/IFileAttachmentService.cs`, `Services/FileAttachmentService.cs`
```csharp
public interface IFileAttachmentService
{
    /// <summary>로컬 파일 경로로부터 Attachment 메타 생성(크기·MIME 추정). 파일 없으면 AgentException.</summary>
    Attachment CreateFromPath(string filePath);

    /// <summary>[#7 서버측·미래] 첨부의 바이트를 base64로 인코딩해 전송 페이로드 준비. 현재 stub(NotImplemented 주석).</summary>
    Task<string> ReadAsBase64Async(Attachment attachment, CancellationToken ct = default);
}
```
**구현 책임:**
- `CreateFromPath`(실제): `FileInfo`로 `SizeBytes`, `FileName=fi.Name`, `ContentType`는 확장자→MIME 간이 매핑(미지정 시 null). 파일 부재 시 `AgentException`.
- `ReadAsBase64Async`(stub): 본문은 `throw new NotImplementedException("서버 첨부 전송은 §8 계약 확정 후 구현")` 또는 주석 + `Task.FromResult("")` — **ServiceEngineer 재량이나 호출되지 않음**(전송 경로 미연결). 인터페이스만 미래 대비.
- 파일 **선택(OpenFileDialog)** 자체는 View 코드비하인드 책임(§5.D). 서비스는 경로→메타 변환만.

### 2.4 `ISuggestionService` / `StubSuggestionService` — stub (G)
파일: `Services/ISuggestionService.cs`, `Services/StubSuggestionService.cs`
```csharp
public interface ISuggestionService
{
    /// <summary>워크스페이스 컨텍스트 기반 동작 힌트. 현재 stub: 항상 빈 목록.</summary>
    Task<IReadOnlyList<Suggestion>> GetSuggestionsAsync(string workspaceRoot, CancellationToken ct = default);
}
```
**구현 책임 (stub):** `StubSuggestionService.GetSuggestionsAsync` → `Task.FromResult<IReadOnlyList<Suggestion>>([])`. 주석으로 §8 엔드포인트(`GET /api/v1/agent/suggestions`) 미래 연동 명시.

---

## 3. AppSettings / SettingsService 변경 (+마이그레이션, 스키마 v4)

### 3.1 AppSettings — §1.7 참조(`UserDisplayName`, `RecentWorkspaces`, SchemaVersion=4).

### 3.2 `ISettingsService` — 메서드 2개 추가
```csharp
Task UpdateUserDisplayNameAsync(string name);             // F
Task UpdateRecentWorkspacesAsync(IReadOnlyList<WorkspaceHistoryEntry> entries);  // B (WorkspaceHistoryService가 호출)
```
> `UpdateRecentWorkspacesAsync`는 `Current.RecentWorkspaces = [..entries]; await SaveAsync(); RaiseSettingsChanged();` 패턴. (WorkspaceHistoryService가 settings를 직접 들고 있으므로 이 메서드 대신 `Current.RecentWorkspaces` 직접 변경 + `SaveAsync` 호출도 허용 — ServiceEngineer 택1, **단 마이그레이션·이벤트 일관성 위해 메서드 경유 권장**.)

### 3.3 SettingsService.LoadAsync — 마이그레이션 v3 → v4
기존 `if (Current.SchemaVersion < 3)` 블록 **다음에** 추가:
```csharp
if (Current.SchemaVersion < 4)
{
    Current.RecentWorkspaces ??= [];
    if (string.IsNullOrEmpty(Current.UserDisplayName))
        Current.UserDisplayName = "";   // 빈 값 유지 → VM에서 Environment.UserName 폴백
    Current.SchemaVersion = 4;
    migrationNeeded = true;  // (현 구조상 return true; 로 통일 — 아래 주의)
}
```
**주의(현 코드 구조):** 현재 `LoadAsync`는 v3 블록에서 `return true;`로 즉시 반환하고 v4 도달 못 함. ServiceEngineer는 마이그레이션을 **누적형**으로 리팩토링:
- `bool migrated = false;` 선언 → 각 버전 블록은 `return` 대신 `migrated = true;`만 설정하고 fall-through → 마지막에 `return migrated;`.
- 즉 v3 블록의 `return true;` → `migrated = true;`로 변경, v4 블록 이어서 실행. **이것이 유일한 기존 로직 변경**(동작 보존: 신규 사용자는 두 블록 다 통과해 v4로 시드).

### 3.4 SettingsService — 신규 메서드 구현
```csharp
public async Task UpdateUserDisplayNameAsync(string name)
{ Current.UserDisplayName = name ?? ""; await SaveAsync(); RaiseSettingsChanged(); }

public async Task UpdateRecentWorkspacesAsync(IReadOnlyList<WorkspaceHistoryEntry> entries)
{ Current.RecentWorkspaces = [.. entries]; await SaveAsync(); RaiseSettingsChanged(); }
```

---

## 4. ViewModel 바인딩 멤버 (UIDesigner가 그대로 바인딩)

### 4.1 `AgentSessionViewModel` (확장) — `ViewModels/AgentSessionViewModel.cs`

**생성자 시그니처 변경** (App.xaml.cs가 신규 서비스 4종 주입):
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

**신규 Observable 프로퍼티:**
| 프로퍼티 | 타입 | 용도 |
|---|---|---|
| `GreetingText` | `string` | F — "{이름}님 안녕하세요, 어떤 업무를 시작할까요?". `SeedFromSettings`에서 `name = settings.Current.UserDisplayName`가 빈 값이면 `Environment.UserName` 사용해 조립. settings 변경 시 재조립. |
| `UserDisplayName` | `string` | F — 표시/편집용(원시 이름). 헤딩은 `GreetingText` 바인딩 권장. |
| `HasAttachments` | `bool` | (선택) `Attachments.Count > 0` 캐시 — UI는 `Attachments.Count` + `CountToVisibility`로 대체 가능. |

**신규 Collections:**
| 컬렉션 | 타입 | 용도 |
|---|---|---|
| `RecentWorkspaces` | `ObservableCollection<WorkspaceHistoryEntry>` | B — 사이드바 "프로젝트" 목록. `workspaceHistory.HistoryChanged` 구독해 Dispatcher로 갱신. |
| `ChatSessions` | `ObservableCollection<ChatSessionSummary>` | C — 사이드바 "채팅" 목록. `RefreshChatSessionsAsync`로 채움. |
| `Attachments` | `ObservableCollection<Attachment>` | D — 컴포저 첨부 칩 ItemsSource. |
| `Suggestions` | `ObservableCollection<Suggestion>` | G — 환영 화면 제안(현재 빈 채로 유지, UI는 비면 숨김). |

**신규 Commands:**
| 커맨드 | 종류 | CanExecute | 동작 |
|---|---|---|---|
| `OpenWorkspaceCommand` | `AsyncRelayCommand<WorkspaceHistoryEntry>` | param!=null && !IsBusy | B — `workspace.SetRoot(e.Path)`; `settings.UpdateWorkspaceRootAsync(e.Path)`; `workspaceHistory.TouchAsync(e.Path)`; `WorkspaceRoot=e.Path`; `GreetingText` 재조립. (settings 변경 이벤트가 App에서 workspace 재동기화하므로 SetRoot 중복 무해.) |
| `RemoveWorkspaceCommand` | `AsyncRelayCommand<WorkspaceHistoryEntry>` | param!=null | B — `workspaceHistory.RemoveAsync(e.Path)`. |
| `LoadChatSessionCommand` | `AsyncRelayCommand<ChatSessionSummary>` | param!=null && !IsBusy | C — 현재 세션 저장(`SaveCurrentSessionAsync`) 후 `chatHistory.LoadAsync(id)` → `RestoreSession(record)`(아래). |
| `DeleteChatSessionCommand` | `AsyncRelayCommand<ChatSessionSummary>` | param!=null | C — `chatHistory.DeleteAsync(id)` + 목록 갱신. 현재 활성 세션이면 `Clear` 후속. |
| `AttachFileCommand` | `RelayCommand` | !IsBusy | D — View 코드비하인드가 OpenFileDialog 띄운 뒤 선택 경로마다 `AddAttachment(path)` 호출. (커맨드 자체는 다이얼로그 요청 신호 — 아래 "다이얼로그 패턴" 참조.) |
| `RemoveAttachmentCommand` | `RelayCommand<Attachment>` | param!=null | D — `Attachments.Remove(a)`. |

**기존 `ClearCommand` 동작 확장 (C):**
- `Clear()` 진입 시 **현재 세션을 먼저 저장**: `await SaveCurrentSessionAsync()` → 기존 초기화 로직 → `RefreshChatSessionsAsync()`로 사이드바 갱신.
- `Clear`는 `RelayCommand`였으나 저장이 async이므로 **`AsyncRelayCommand`로 승격**(바인딩명 `ClearCommand` 유지 → UIDesigner 무변경).

**기존 `SendAsync` 동작 확장 (C, D):**
- 전송 직전: 현재 `Attachments` 스냅샷을 `UserTurnViewModel`에 표시용으로 넘기고(선택), `_session.Messages`에 추가될 user 메시지에 `Attachments`(예약 필드) 부착 — **단 서버 전송은 미연결**이므로 `AgentMessage.User(goal)`에 attachments를 실으려면 orchestrator가 user 메시지를 만든다(orchestrator 무변경 원칙). → **MVP 절충:** 첨부 칩은 UI에 표시·관리하되, 전송 시 `Attachments`를 goal 텍스트에 "[첨부: file1, file2]" 형태로 append하거나 그대로 비우고 전송 후 `Attachments.Clear()`. **확정:** 전송 후 `Attachments.Clear()` 호출, 실제 페이로드 부착은 §8 계약 확정까지 보류(주석). (ViewModelEngineer: orchestrator 시그니처 불변 유지가 우선.)
- 턴 완료(`AgentDone` 투영 후 `finally`) 또는 `SendAsync` 종료 시: `await SaveCurrentSessionAsync()` + `RefreshChatSessionsAsync()`.

**신규 private 헬퍼 (ViewModelEngineer 구현):**
```csharp
private async Task SaveCurrentSessionAsync();      // _session.Messages가 비면 no-op. record 구성(Id=_session.Id, Title=chatHistory.BuildTitle(...), Updated=now, WorkspaceRoot, Messages=_session.Messages 스냅샷) → chatHistory.SaveAsync.
private async Task RefreshChatSessionsAsync();     // chatHistory.ListAsync → ChatSessions 재구성(Dispatcher).
private void RestoreSession(ChatSessionRecord r);  // _session 교체 + Transcript 재투영(아래 4.1.1).
private void AddAttachment(string path);           // attachments.CreateFromPath(path) → Attachments.Add (중복 경로 무시).
```

#### 4.1.1 `_session` 교체 / Transcript 재투영 (C 복원)
- `AgentSession`은 `Id`가 get-only(Guid 자동) → 복원 시 **임의 Id로 새 세션 생성 불가**. 두 옵션 중 택1, **ServiceEngineer가 AgentSession에 최소 변경**:
  - **(택1·권장)** `AgentSession`에 생성자 추가: `public AgentSession(string id, IEnumerable<AgentMessage> messages)` — `Id` set 허용(`init` 또는 ctor 주입). `Messages.AddRange(messages)`. 기존 `new AgentSession()` 경로 보존.
  - (택2) VM이 `new AgentSession()` 후 `Messages` 리스트에 record.Messages를 AddRange(Id는 새 Guid가 되나, 저장 시 record.Id를 별도 보존). → **택1이 깔끔**, 본 스펙은 택1 채택.
- `RestoreSession`: `_session = new AgentSession(r.Id, r.Messages)`; `Transcript.Clear()`; r.Messages 순회하며 role별 투영:
  - `User` → `UserTurnViewModel{Text=Content}`
  - `Assistant`(Content 있음) → `AssistantTurnViewModel{Text=Content, IsStreaming=false}`. ToolCalls는 다음 Tool 메시지와 짝지어 `ToolCallViewModel` 복원(가능 범위; MVP는 assistant 텍스트 + tool 결과 카드만 복원해도 허용).
  - `Tool` → 직전 ToolCall과 매칭해 `ToolCallViewModel{Status=Succeeded/Failed by IsError, ResultText=Content}`.
  - `System` → 스킵(시스템 프롬프트는 표시 안 함).

#### 4.1.2 다이얼로그 패턴 (D — MVVM 안전)
- `AttachFileCommand`는 VM이 직접 `OpenFileDialog`를 열지 않는다. **View 코드비하인드**(MainWindow.xaml.cs)가 `+` 버튼 Click 핸들러에서 `Microsoft.Win32.OpenFileDialog`(Multiselect=true) → 선택 경로마다 `vm.AddAttachmentPublic(path)` 호출.
- 따라서 VM에 **public 진입점** 노출: `public void AddAttachmentPublic(string path) => AddAttachment(path);` (UIDesigner/코드비하인드가 호출). `AttachFileCommand`는 생략 가능하나 **버튼 IsEnabled 게이트용으로 유지**(CanExecute=`!IsBusy`). UIDesigner는 `+` 버튼에 `Click` 핸들러 + `IsEnabled="{Binding IsBusy, Converter=InverseBool}"` 둘 다 적용.

**`SeedFromSettings` 확장:** 기존 3필드 시드에 더해:
```csharp
var rawName = string.IsNullOrWhiteSpace(s.UserDisplayName) ? Environment.UserName : s.UserDisplayName;
UserDisplayName = rawName;
GreetingText = $"{rawName}님 안녕하세요, 어떤 업무를 시작할까요?";
```

**`InitializeAsync` 확장:** 기존 health-check 뒤에 `RefreshWorkspaceList()`(workspaceHistory.GetRecent → RecentWorkspaces) + `await RefreshChatSessionsAsync()` + `_ = LoadSuggestionsAsync()`(suggestions.GetSuggestionsAsync(WorkspaceRoot) → Suggestions; 현재 빈 목록).

### 4.2 `SettingsViewModel` (확장) — `ViewModels/SettingsViewModel.cs` (F 선택)
- 생성자/기존 멤버 무변경. **신규 1프로퍼티 + 저장 연결**:
```csharp
[ObservableProperty] private string _userDisplayName = string.Empty;   // ctor에서 c.UserDisplayName 시드
```
- 저장: 기존 `SaveServerConfigCommand`에 합류하지 말고 **별도** `SaveUserProfileCommand`(AsyncRelayCommand → `_settings.UpdateUserDisplayNameAsync(UserDisplayName)`). (UIDesigner가 설정창에 "사용자 이름" 입력 + 저장 버튼 추가 — 선택 사항이므로 미구현 시에도 빌드 영향 없음.)
- ctor 시드: `UserDisplayName = c.UserDisplayName;`

---

## 5. UI 변경 지시 (UIDesigner)

> 모든 신규 목록은 **`ItemsControl` + `DataTemplate`**, 빈 상태는 `CountToVisibility`/`EmptyCountToVisibility` 재사용.

### MainWindow.xaml

**A. 삭제 (사이드바 상단 네비):** "검색"(L58–64)·"플러그인"(L66–72)·"자동화"(L74–80)·"모바일"(L82–88) 4개 `Button` 제거. **"새 채팅"(L48–56, ClearCommand)·"설정"(L142–149) 유지.**

**B. 프로젝트 섹션 교체 (L94–120):** 정적 WorkspaceRoot/현재작업공간 버튼 → 동적 목록:
```xml
<TextBlock Text="프로젝트" Style="{StaticResource SidebarSectionHeader}"/>
<ItemsControl ItemsSource="{Binding RecentWorkspaces}">
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <!-- 폴더 글리프 E8B7 + DisplayName, 클릭=OpenWorkspaceCommand(항목), 우측 제거 버튼=RemoveWorkspaceCommand(항목) -->
      <Button Style="{StaticResource SidebarItemButton}"
              Command="{Binding DataContext.OpenWorkspaceCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
              CommandParameter="{Binding}" ToolTip="{Binding Path}">
        ... DisplayName 표시 + (선택) 제거 X 버튼 ...
      </Button>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
<TextBlock Text="최근 작업 공간 없음" Foreground="{StaticResource TextMuted}" FontSize="13" Margin="10,2,0,0"
           Visibility="{Binding RecentWorkspaces.Count, Converter={StaticResource EmptyCountToVisibility}}"/>
```

**C. 채팅 섹션 교체 (L122–137):** 정적 "현재 세션" → 동적 목록:
```xml
<TextBlock Text="채팅" Style="{StaticResource SidebarSectionHeader}"/>
<ItemsControl ItemsSource="{Binding ChatSessions}">
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <!-- 문서 글리프 E8BD + Title, 클릭=LoadChatSessionCommand(항목), 우측 삭제=DeleteChatSessionCommand(항목) -->
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
<TextBlock Text="채팅 없음" Foreground="{StaticResource TextMuted}" FontSize="13" Margin="10,2,0,0"
           Visibility="{Binding ChatSessions.Count, Converter={StaticResource EmptyCountToVisibility}}"/>
```

**F. 환영 헤딩 교체 (L247–253):** `<Run WorkspaceRoot/>...무엇을 작업할까요?` → 단일 바인딩:
```xml
<TextBlock Text="{Binding GreetingText}" TextAlignment="Center" TextWrapping="Wrap" .../>
```

**G. 제안 정적 블록 교체 (L255–278):** 정적 3개 Button 제거 → 동적(빈 목록이면 숨김):
```xml
<ItemsControl ItemsSource="{Binding Suggestions}"
              Visibility="{Binding Suggestions.Count, Converter={StaticResource CountToVisibility}}">
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <Button Style="{StaticResource SuggestionItemButton}"
              Command="{Binding DataContext.??, ...}" CommandParameter="{Binding}">
        <!-- Icon(있으면) + Text. 클릭 시 InputText=Prompt 채우기 — 선택. 현재 빈 목록이라 렌더 안 됨 -->
      </Button>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```
> 클릭 시 동작은 미정(서버 힌트 도착 후). MVP는 표시만(빈 목록 → 미렌더). 별도 `ApplySuggestionCommand`는 **불요**(목록 비어 있음).

**D. 첨부 칩 + 버튼 (컴포저, L368–422 영역):**
- 입력(Row0)과 툴바(Row1) 사이 또는 입력 위에 **첨부 칩 ItemsControl** 추가:
```xml
<ItemsControl ItemsSource="{Binding Attachments}"
              Visibility="{Binding Attachments.Count, Converter={StaticResource CountToVisibility}}">
  <ItemsControl.ItemsPanel><ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <!-- 칩: FileName + 제거 X 버튼(RemoveAttachmentCommand, CommandParameter={Binding}) -->
      <Border Style="{StaticResource Chip}" ...>
        <StackPanel Orientation="Horizontal">
          <TextBlock Text="{Binding FileName}" .../>
          <Button Content="&#xE8BB;" Command="{Binding DataContext.RemoveAttachmentCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}" CommandParameter="{Binding}" .../>
        </StackPanel>
      </Border>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```
- "+" 버튼(L380–382): `IsEnabled="False"` 제거 → `Click="AttachButton_Click"`(코드비하인드 OpenFileDialog) + `IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBool}}"`. ToolTip "첨부".

**E. 마이크 삭제:** 컴포저 툴바의 마이크 버튼(L402–405) + 해당 `ColumnDefinition Width="Auto"`(L375 "마이크" 컬럼) 제거. 전송 버튼 컬럼 인덱스 재정렬(Column 5→4 등) 또는 컬럼 정의만 1개 제거하고 나머지 Grid.Column 값 조정.

### MainWindow.xaml.cs
- 신규 핸들러: `AttachButton_Click` → `new Microsoft.Win32.OpenFileDialog { Multiselect = true }` → `ShowDialog()==true`면 `FileNames` 순회하며 `(DataContext as AgentSessionViewModel)?.AddAttachmentPublic(path)`.
- 기존 `MenuItem_HotkeySettings_Click` 등 무변경.

### ChatOnlyWindow.xaml
- 사이드바 없음(컴팩트) → B/C 목록 **미적용**.
- **D 첨부**(선택, 컴팩트): 입력바에 "+" 추가 가능하나 **MVP 범위 외**(스펙은 MainWindow 컴포저 기준). ChatOnlyWindow는 **무변경 허용**(마이크 버튼 없음 — E 무관, 제안/네비 없음 — A/G 무관). 단 `GreetingText`는 ChatOnlyWindow에 환영 화면 없으므로 무관.
- **결론: ChatOnlyWindow.xaml 변경 없음.**

---

## 6. App.xaml.cs 와이어링 (ServiceEngineer)

`OnStartup`에서 신규 서비스 생성 순서(기존 번호 사이 삽입):

```
[7 이후] 8b) WorkspaceHistoryService
    var workspaceHistory = new WorkspaceHistoryService(_settingsService);

[10 이전] 9b) ChatHistoryService / FileAttachmentService / SuggestionService
    var chatHistory  = new ChatHistoryService();
    var attachments  = new FileAttachmentService();
    var suggestions  = new StubSuggestionService();

[10] 루트 VM — 생성자에 4종 추가 (§4.1 시그니처):
    _mainVm = new AgentSessionViewModel(
        orchestrator, _api, permissions, workspace, _settingsService,
        workspaceHistory, chatHistory, attachments, suggestions);
```

**의존성:** `WorkspaceHistoryService`←`ISettingsService`. 나머지 3종 무의존(파라미터리스 ctor). `ChatHistoryService`/`StubSuggestionService`/`FileAttachmentService`는 외부 상태 없음.

**필드 보관(선택):** App에 `IWorkspaceHistoryService? _workspaceHistory` 등 필드 추가는 불필요(단일 사용). 단 **앱 종료 시 세션 저장**(C)을 원하면 `OnExit`에서 `_mainVm?.SaveCurrentSessionOnExit()` 호출 — ViewModelEngineer가 동기 best-effort 저장 메서드 제공(선택). MVP는 `AgentDone`/`Clear` 시 저장으로 충분 → **OnExit 저장은 선택 사항.**

**기존 §15 SettingsChanged 핸들러(B 연동):** 현재 `workspace.SetRoot(s.WorkspaceRoot)` 수행. 여기에 **워크스페이스 자동 Add** 부착:
```csharp
_settingsService.SettingsChanged += (_, s) =>
{
    workspace.SetRoot(s.WorkspaceRoot);
    if (!string.IsNullOrWhiteSpace(s.WorkspaceRoot))
        _ = workspaceHistory.AddAsync(s.WorkspaceRoot);   // 설정 변경 시 히스토리 자동 기록
    _globalHotkey!.Unregister();
    _globalHotkey.Register(s.Hotkey);
};
```
> 주의: `AddAsync`가 `SaveAsync`+`RaiseSettingsChanged`를 부르면 **재진입 루프** 위험. → `WorkspaceHistoryService.AddAsync`는 `RecentWorkspaces`만 변경하고 `SaveAsync()`는 부르되 **`RaiseSettingsChanged`는 부르지 않음**(또는 `HistoryChanged` 전용 이벤트만 발생). ServiceEngineer는 `UpdateRecentWorkspacesAsync`(RaiseSettingsChanged 호출) 대신 **`Current.RecentWorkspaces` 직접 변경 + `settings.SaveAsync()` + `HistoryChanged` 발생** 경로를 사용해 settings의 `SettingsChanged` 루프를 회피. **이 점 명시적 구현 요구.**

---

## 7. 빌드 검증 메모 (ServiceEngineer/QA)
- `AppSettings`는 Newtonsoft로 직렬화됨. 신규 `RecentWorkspaces`(List<record>)·`UserDisplayName`은 Newtonsoft 기본 직렬화 호환(record + init 프로퍼티 Newtonsoft 13+ 지원). QA: 라운드트립 확인.
- `AgentMessage.Attachments` 추가는 `AgentJson.Options`(STJ, WhenWritingNull)로 기존 요청 바이트 불변 — 서버 회귀 없음.
- 마이그레이션 누적형 리팩토링(§3.3) 후 신규/기존 사용자 모두 v4 시드되는지 QA.

---

## 8. docs/API_CONTRACT.md 추가 섹션 설계 (#7 서버 계약)

기존 `## 7. 서버 개발팀에 전달할 핵심 요청사항`(L276) **뒤, L285 `---` 앞**에 **신규 섹션 `## 8` 삽입**(또는 §7 끝에 항목 추가 + §8 신설). 권장: **§8 신설**.

```markdown
## 8. Phase D 확장 — 향후 서버 기능 (클라이언트 stub/예약 필드 선반영)

> 아래 3기능은 클라이언트가 인터페이스/DTO/예약 필드를 **미리** 정의했고, 서버 구현 시 그대로 연동된다.
> 현재 클라이언트 동작: 동작 힌트=빈 목록 stub, 첨부=로컬 관리만(전송 미연결), 채팅 히스토리=로컬 영속.

### 8.1 GET /api/v1/agent/suggestions — 동작 힌트 (요구 G)
- Query: `?workspace_root={path}` (선택)
- 200 Response:
  ```json
  { "suggestions": [ { "text": "현재 프로젝트의 버그를 찾아 수정해 보세요", "prompt": "이 워크스페이스의 버그를 점검해줘", "icon": "E9D5" } ] }
  ```
- 클라이언트: `ISuggestionService.GetSuggestionsAsync(workspaceRoot, ct)` → `IReadOnlyList<Suggestion>`. 엔드포인트 부재 시 빈 목록(현 stub).

### 8.2 첨부 전송 — POST /api/v1/agent/chat 확장 (요구 D 서버측)
- 요청 §4.1 `messages[].` 에 **`attachments[]` 필드 예약**:
  ```json
  { "role": "user", "content": "이 파일 분석해줘",
    "attachments": [ { "file_name": "report.pdf", "content_type": "application/pdf", "size_bytes": 10240, "data_base64": "..." } ] }
  ```
- 클라이언트 현재: `AgentMessage.Attachments`(file_path/file_name/size_bytes/content_type)만 보유, `data_base64`는 미전송(`IFileAttachmentService.ReadAsBase64Async` stub).
- 서버 합의 필요: 최대 파일 크기, 허용 MIME, base64 inline vs 멀티파트 업로드 엔드포인트(`POST /api/v1/agent/attachments` 별도 권장).

### 8.3 채팅 히스토리 서버 동기화 (요구 C 서버측, 선택)
- 현재 로컬 `%APPDATA%/OhMyAgent/sessions/{id}.json` 단독. 미래 동기화용 엔드포인트 초안:
  - `GET /api/v1/agent/sessions` → `ChatSessionSummary[]`
  - `GET /api/v1/agent/sessions/{id}` → `ChatSessionRecord`
  - `PUT /api/v1/agent/sessions/{id}` (upsert) / `DELETE /api/v1/agent/sessions/{id}`
- 클라이언트: `IChatHistoryService`가 추상화 경계 — 로컬 구현을 서버 구현으로 교체 가능. 현재 미연동.

### 8.4 서버팀 결정 요청
1. 동작 힌트 엔드포인트 제공 여부/스키마.
2. 첨부 전송 방식(inline base64 vs 별도 업로드) + 한도.
3. 채팅 히스토리 서버 보관 정책(클라 단독 유지 vs 동기화).
```

---

## 9. 파일 매니페스트

### CREATE (Models)
- `Models/WorkspaceHistoryEntry.cs`
- `Models/ChatSessionRecord.cs`
- `Models/ChatSessionSummary.cs`
- `Models/Attachment.cs`
- `Models/Suggestion.cs`

### CREATE (Services — 인터페이스 + 구현)
- `Services/IWorkspaceHistoryService.cs` · `Services/WorkspaceHistoryService.cs`  (실제)
- `Services/IChatHistoryService.cs` · `Services/ChatHistoryService.cs`  (실제, 로컬 JSON)
- `Services/IFileAttachmentService.cs` · `Services/FileAttachmentService.cs`  (클라 실제 + 전송 stub)
- `Services/ISuggestionService.cs` · `Services/StubSuggestionService.cs`  (stub)

### MODIFY (Models)
- `Models/AppSettings.cs`  (UserDisplayName, RecentWorkspaces, SchemaVersion=4)
- `Models/Agent/AgentMessage.cs`  (Attachments 예약 필드)

### MODIFY (Services)
- `Services/ISettingsService.cs`  (UpdateUserDisplayNameAsync, UpdateRecentWorkspacesAsync)
- `Services/SettingsService.cs`  (v4 마이그레이션 누적형 리팩토링 + 신규 메서드)
- `Services/AgentSession.cs`  (복원용 ctor `AgentSession(string id, IEnumerable<AgentMessage>)`)

### MODIFY (ViewModels)
- `ViewModels/AgentSessionViewModel.cs`  (ctor 4종 주입 + GreetingText/UserDisplayName + RecentWorkspaces/ChatSessions/Attachments/Suggestions + 6 신규 커맨드 + Clear/Send 확장 + 헬퍼)
- `ViewModels/SettingsViewModel.cs`  (UserDisplayName + SaveUserProfileCommand — F 선택)

### MODIFY (Views)
- `MainWindow.xaml`  (A 네비 4삭제 / B·C 동적목록 / D 첨부칩+버튼 / E 마이크삭제 / F 인사 / G 제안 동적)
- `MainWindow.xaml.cs`  (AttachButton_Click + OpenFileDialog)
- `App.xaml.cs`  (신규 서비스 4종 생성·주입 + SettingsChanged에 workspaceHistory.AddAsync)

### UNCHANGED (명시)
- `Views/ChatOnlyWindow.xaml` / `.cs`  (사이드바 없음, 마이크 없음 — 델타 무관)
- `Services/AgentOrchestrator.cs` / `IAgentOrchestrator.cs`  (시그니처 불변 — 세션 복원은 VM이 처리)
- `Resources/Converters.xaml`, `Views/Converters.cs`  (기존 CountToVisibility/EmptyCountToVisibility/InverseBool 재사용, 신규 컨버터 불요)

---

## 10. 의존성 다이어그램

```
View(MainWindow)
  └─ AgentSessionViewModel
       ├─ IAgentOrchestrator (기존, 무변경)
       ├─ IAgentApiClient / IPermissionService / IWorkspaceContext / ISettingsService (기존)
       ├─ IWorkspaceHistoryService ──→ ISettingsService (RecentWorkspaces 영속)         [B 실제]
       ├─ IChatHistoryService ──→ %APPDATA%/OhMyAgent/sessions/*.json (AgentJson)        [C 실제]
       ├─ IFileAttachmentService ──→ 로컬 파일 메타(실제) / 전송 base64(stub)            [D 클라 실제]
       └─ ISuggestionService ──→ (stub: 빈 목록) ⟶ 미래 GET /suggestions                 [G stub]

Models: WorkspaceHistoryEntry · ChatSessionRecord(+Summary) · Attachment · Suggestion
        AgentMessage(+Attachments 예약) · AppSettings(+RecentWorkspaces/UserDisplayName, v4)
```

## 11. 구현 제외 범위 (이번 델타에서 하지 않음)
- 첨부의 **실제 서버 전송**(base64 페이로드 빌드·업로드) — §8.2 stub.
- 제안의 **실제 서버 조회·클릭 적용 로직** — §8.1 stub(빈 목록).
- 채팅 히스토리 **서버 동기화** — §8.3 인터페이스 경계만.
- ChatOnlyWindow의 사이드바/첨부 UI.
- 트랜스크립트 **완전 복원**(ToolCall↔Tool 페어링 100%) — MVP는 텍스트+결과 카드 복원 허용(§4.1.1).
