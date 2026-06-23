# 02 — ServiceEngineer Summary (Models + Services + Phase 0 + App wiring)

## PART A — Phase 0 cleanup (완료)
**삭제(12 파일):** Services/{AgentActionService, IAgentActionService, McpSseServer, IMcpSseServer, McpRemoteAgentService, IRemoteAgentService, ChatService, IChatService}.cs + Models/Mcp/{McpRequest, McpResponse, McpError, McpTool}.cs.
- `ViewModels/MainViewModel.cs` 는 ViewModelEngineer 가 이미 삭제함 (내가 건드리지 않음).
- KEEP: ScriptExecutor, SecurityValidator, Models/Mcp/{ScriptResult, ScriptType, ValidationResult}.

**csproj:** `Microsoft.SemanticKernel` PackageReference 제거. CommunityToolkit.Mvvm / Newtonsoft.Json / System.Drawing.Common 유지.

**ScriptExecutor / IScriptExecutor:** `ExecutePowerShellAsync`/`ExecuteCmdAsync` 에 `string? workingDirectory = null` 파라미터 추가 (CancellationToken 뒤로 이동 → 이제 `(script, timeoutMs, workingDirectory, ct)`). 기존 호출 호환은 named-arg 로 처리.

**ChatWindowCoordinator:** `Func<MainViewModel>` → `Func<AgentSessionViewModel>` 로 제네릭 변경.

## PART B — Models (Models/Agent/, namespace ...Models)
- AgentEnums.cs: MessageRole, ToolRisk, PermissionMode, PermissionDecision, StopReason
- AgentMessage.cs (sealed record + System/User/Assistant/ToolResultMsg 팩토리)
- ToolCall.cs, ToolSchema.cs, RequestMetadata.cs, AgentRequest.cs, Usage.cs, ModelInfo.cs
- AgentStreamEvent.cs (MessageStart/ContentDelta/ToolCallEvent/MessageStop/ErrorEvent)
- AgentEvent.cs (AgentTextDelta/AssistantMessageComplete/ToolCallStarted/AwaitingApproval/ToolCallResult/IterationAdvanced/Done/Error)
- ToolResult.cs (Ok/Fail/Json 팩토리; Json 은 AgentJson.Options 사용)
- AppSettings.cs: McpPort/McpEnabled 제거, SchemaVersion=3, WorkspaceRoot/PermissionMode/MaxIterations/ServerBaseUrl/AuthScheme/AuthToken/ModelId/MaxTokens 추가.

> 모든 와이어 DTO 는 System.Text.Json + `[JsonPropertyName]` snake_case. AppSettings 만 Newtonsoft(기존 유지).

## PART C — Services (namespace ...Services)
- **AgentJson.cs**: 공유 JsonSerializerOptions (Web defaults + JsonStringEnumConverter(SnakeCaseLower) + ignore null).
- **IAgentApiClient/AgentApiClient**: POST /api/v1/agent/chat SSE 파싱(event/data 라인 버퍼링, blank-line dispatch), message_start/content_delta/tool_call/message_stop/error 매핑. Auth: Bearer→Authorization, ApiKey→X-Api-Key (토큰 비면 생략). HTTP non-2xx → `{error:{code,message}}` 파싱해 단일 ErrorEvent yield. 전송 실패는 AgentException. CheckHealthAsync, GetModelsAsync({models:[]} 파싱, 실패 시 빈 목록).
- **ITool / IToolRegistry / ToolRegistry**: ToolRegistry(IEnumerable<ITool>) — name→tool 사전(대소문자 무시), ToSchemas() 캐시.
- **ToolContext.cs** (Services 레이어, 서비스 참조 보유): record(IWorkspaceContext, PermissionMode).
- **IWorkspaceContext/WorkspaceContext**: Root 정규화(빈 값→Desktop), ResolvePath(상대→Root 결합, GetFullPath, 탈출 시 AgentException), IsInsideWorkspace(대소문자 무시 prefix), SetRoot.
- **IPermissionService/PermissionService**: FullAuto→Allow; ReadOnly→Manual/AutoSafe 모두 Allow; 그 외 세션 AlwaysAllow(도구명 HashSet) → 없으면 핸들러; 핸들러 없으면 Deny. AlwaysAllow 결정은 set 추가 후 Allow 반환. ClearSessionRules.
- **IAgentOrchestrator/AgentOrchestrator**: 전체 루프 (system 시드 → user → MaxIterations 캡 → SSE 스트림 → tool_use 판정 → 도구 실행/권한/승인 이벤트 → tool 결과 append → 재요청). 취소→AgentError("cancelled"), 최대반복→AgentError("max_iterations"). yield-내-try/catch 제약 회피용 `SafeStream` 내부 래퍼 사용.
- **AgentSession.cs** (Services): Id/Messages/LastUsage + DefaultSystemPrompt(workspaceRoot, mode).
- **SettingsService/ISettingsService**: v2→v3 마이그레이션(MCP 필드 drop, 신규 기본값), UpdateWorkspaceRootAsync/UpdatePermissionModeAsync/UpdateServerConfigAsync 추가.

### Tools (Services/Tools/) — 11개 ITool + 헬퍼
ToolSchemas.cs(Parse/Get*), GlobMatcher.cs(경량 **/*/? glob, 외부 라이브러리 없음).
| 도구 | Risk |
|------|------|
| run_command | Execute (SecurityValidator + ScriptExecutor, WorkingDirectory=Root) |
| read_file | ReadOnly (1-based 라인 슬라이스, ~200KB 캡) |
| write_file | Write (부모 디렉토리 생성, UTF-8 no-BOM) |
| edit_file | Write (old_string 유일성 검증, replace_all) |
| list_directory | ReadOnly |
| glob | ReadOnly (1000 캡) |
| grep | ReadOnly (정규식, 500 매치 캡) |
| create_directory | Write |
| move / copy / delete | Destructive |

## PART F — App.xaml.cs (DI 재배선)
PART F 순서대로: SettingsService.LoadAsync → HttpClient(BaseAddress=ServerBaseUrl) → WorkspaceContext → ScriptExecutor → 11 tools → ToolRegistry → PermissionService → AgentApiClient → AgentOrchestrator → AgentSessionViewModel → MainWindow → Tray → ChatWindowCoordinator → GlobalHotkey → SettingsChanged(workspace.SetRoot + 핫키 재등록) → Show + InitializeAsync.
- `_mcpService` 필드 제거, `_mainVm` 타입 AgentSessionViewModel, `_api` 필드 추가.
- OnExit 동기화(MCP 블록 제거).
- 트레이 Settings 메뉴: `new SettingsViewModel(_settingsService, _api)`.

## ViewModelEngineer 가 지켜야 할(이미 일치 확인됨) 계약
- `AgentSessionViewModel(IAgentOrchestrator, IAgentApiClient, IPermissionService, IWorkspaceContext, ISettingsService)` — App.xaml.cs 가 이 시그니처로 생성. **현재 코드와 일치 확인.**
- `SettingsViewModel(ISettingsService, IAgentApiClient)` — App.xaml.cs 가 이 시그니처로 생성. **현재 코드와 일치 확인.**
- PermissionService.SetApprovalHandler 시그니처: `Func<ToolCall, ToolRisk, CancellationToken, Task<PermissionDecision>>` (VM 의 RequestApprovalAsync 와 일치).

## 빌드 상태
Services/Models/App 컴파일 오류 0. 남은 빌드 오류는 **UIDesigner 소유** 파일 2개뿐:
- MainWindow.xaml.cs(13): `MainViewModel` 미존재 → AgentSessionViewModel 로 변경 필요.
- Views/ChatOnlyWindow.xaml.cs(12): 동일.
이는 PART E(UIDesigner) 작업 범위이며 내 레이어와 무관.

## 추가 결정/가정
- `Path.GetRelativePath` 슬래시 정규화 후 glob 매칭. GlobMatcher 는 외부 의존성 없이 정규식 변환.
- write_file/edit_file 는 UTF-8(BOM 없음)으로 기록.
- AppSettings 의 PermissionMode 는 Newtonsoft 기본(정수) 직렬화 — 라운드트립 무해.
- ScriptExecutor 시그니처 변경으로 파라미터 순서가 `(…, workingDirectory, ct)` 가 됨 — 기존 호출처는 모두 삭제됐고 신규 호출(RunCommandTool)은 named-arg 사용.
