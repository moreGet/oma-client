# 05 — QA Report: Phase 0 (Cleanup) + Phase 1 (Agent Loop)

## 검증 결과: **PASS**

전 레이어(Models/Services/ViewModels/Views) 교차 검증 완료. 빌드 0 오류, 명세(01_architect_spec.md)·API_CONTRACT 준수 확인. 보안 핵심 항목(샌드박스·권한 게이트)·에이전트 루프·SSE 파서 모두 계약대로 동작. 클린업(Phase 0)으로 삭제된 타입에 대한 잔존 참조 0건. **직접 수정한 항목 없음** — 명백한 결함(컴파일 오류/널 안전/디스패처 위반/샌드박스 탈출/루프·파싱 버그)이 발견되지 않았다.

---

## 빌드 결과

```
빌드: 오류 0개, 경고 2개
```

- 명령: `dotnet build OhMyAgent.AiAgent.Client.csproj` (net10.0-windows)
- 경고 2건 모두 **NU1510** — `System.Drawing.Common` 패키지가 SDK에 내장되어 pruning되지 않는다는 정보성 경고. 트레이 아이콘 생성(`Bitmap`/`Icon`, App.xaml.cs)에 실제로 사용되므로 제거 불가 또는 무해. **블로커 아님.**

---

## 검증 항목별 결과

| # | 검증 항목 | 결과 | 비고 |
|---|----------|------|------|
| 1 | 빌드 무결성 | PASS | 0 오류 / 경고 2(NU1510, 무해) |
| 2 | 에이전트 루프 정확성 (AgentOrchestrator) | PASS | send→parse→tool_use 분기→권한 게이트+레지스트리 실행→tool 결과 append→재요청→end_turn/MaxIterations 종료 전부 계약대로 |
| 3 | SSE 파싱 (AgentApiClient) | PASS | event:/data: 라인 버퍼, 빈 줄 경계 dispatch, 멀티 data: 라인 `\n` 결합, 스트림 종료 시 tail dispatch 모두 정상 |
| 4 | 권한 게이트 (Manual) | PASS | ReadOnly 자동허용 / Write·Execute·Destructive 승인필요 / AlwaysAllow 세션 지속 / Deny→오류 ToolResult 피드백(크래시 아님) |
| 5 | 워크스페이스 샌드박스 (WorkspaceContext) | PASS | `..`·절대경로 탈출 차단(GetFullPath + 대소문자 무시 prefix StartsWith). 심볼릭/정션은 한계 → 권고 R1 |
| 6 | 11개 도구 구현 | PASS | 스키마↔파라미터 파싱 일치, 널 안전, 예외→ToolResult(is_error) 변환(오케스트레이터 중앙 처리), run_command가 ScriptExecutor+SecurityValidator를 workingDirectory=workspace로 재사용 |
| 7 | MVVM / WPF 정합성 | PASS | toolkit 제너레이터 INotifyPropertyChanged, ObservableCollection을 Dispatcher로 마샬, AsyncRelayCommand, XAML 바인딩↔VM 멤버 일치, DataTemplateSelector 배선 정상 |
| 8 | Dead 참조 (삭제 타입) | PASS | MainViewModel/ChatService/AgentActionService/McpSseServer/McpRemoteAgentService/SemanticKernel/Mcp* 참조 0건 |

---

## 발견된 문제 (직접 수정)

없음. 명백한 결함이 발견되지 않아 코드 수정을 수행하지 않았다.

---

## 상세 검증 노트

### 에이전트 루프 (AgentOrchestrator.cs)
- 시스템 프롬프트 시드(세션 비었을 때만) → user goal append → `while (iteration < max && !ct.IsCancellationRequested)`. **MaxIterations 캡이 while 조건에서 실제로 루프를 멈춘다.** 루프 정상 탈출 후 `iteration >= max`면 `AgentError("max_iterations", ...)` 방출.
- `MessageStop.StopReason == "tool_use"` (또는 tool_call 1개 이상 수신 시 방어적 OR)일 때만 도구 실행 후 `iteration++` 하고 재요청. 그 외(`end_turn`/`max_tokens`/없음)는 `AgentDone` 방출 후 `yield break`. CONTRACT §4.2 stop_reason 표와 일치.
- **Stateless 대화 기록**: `BuildRequest`가 매 반복마다 `session.Messages` 전체 스냅샷을 전송(CONTRACT §4). assistant 턴(text+tool_calls)·tool 결과 턴이 순서대로 append되어 멀티턴 도구 추론 가능.
- **CancellationToken 전파**: Stop→`_cts.Cancel()`이 `RunAsync`/`SendAsync`/`ExecuteAsync`로 전파. 스트림 루프·도구 루프·연결 단계에서 `ct.IsCancellationRequested` 체크 후 `AgentError("cancelled", ...)` 방출하고 enumerable 밖으로 예외를 재던지지 않음(C.5 5번 준수). `OperationCanceledException`은 `ExecuteCallAsync`와 `SafeStream`에서 흡수.
- `SafeStream` 래퍼로 `yield` 내부 try/catch 제약을 우회하면서 연결 실패(AgentException)·스트림 오류를 안전하게 단일 `StreamFailure`로 전환 — 견고한 설계.

### SSE 파서 (AgentApiClient.cs)
- 라인 단위 read, `event:`/`data:` 접두 파싱, 빈 줄에서 이벤트 경계 dispatch, 여러 `data:` 라인은 `\n`으로 결합(부분/멀티라인 버그 없음). 스트림 EOF 시 잔여 버퍼 마지막 dispatch.
- `data:` 페이로드는 JSON이므로 `TrimStart()`가 선행 공백을 제거해도 JSON 의미는 보존된다(텍스트 토큰 내부 공백은 JSON 문자열 값으로 안전). `content_delta` 토큰 손실 없음.
- 비2xx HTTP는 `{error:{code,message}}` 파싱 후 단일 `ErrorEvent` 방출, 전송 실패만 `AgentException` throw(C.1 결정사항 준수). `message_stop`의 usage 누락 시 `Usage(0,0)` 폴백.

### 샌드박스 (WorkspaceContext.cs)
- `ResolvePath`: 상대경로는 Root와 결합, `Path.GetFullPath`로 `..` 정규화, `IsInsideWorkspace` 실패 시 `AgentException` throw. 절대경로가 Root 밖이면 차단.
- `IsInsideWorkspace`: Root 자기 자신 허용 + `Root + DirectorySeparator` prefix 대소문자 무시 StartsWith. 형제 디렉토리 prefix 오탐(`proj` vs `proj_evil`) 방지됨(구분자 강제).
- 11개 도구 전부 파일 경로 인자를 `ctx.Workspace.ResolvePath(...)`로 통과시켜 탈출 차단. Glob/Grep도 ResolvePath된 baseDir 하위만 열거.

### 권한 게이트 (PermissionService.cs)
- FullAuto→항상 Allow / ReadOnly→Manual·AutoSafe 모두 자동 Allow / 세션 AlwaysAllow(HashSet<도구명>, 락 보호) / 핸들러 없으면 gated risk는 Deny(headless 안전). AlwaysAllow 결정 시 `call.Name` 세션 등록.
- `ApprovalRequestViewModel.WaitForDecisionAsync`가 `ct` 취소를 Deny로 처리(크래시 아님). Deny→오케스트레이터가 `ToolResultMsg(..., isError:true)` append하여 모델에 피드백.

### MVVM/WPF (AgentSessionViewModel.cs 외)
- 오케스트레이터는 thread-free, VM이 `UiInvokeAsync`로 모든 Transcript/프로퍼티 변경을 Dispatcher에 마샬(CheckAccess 분기). ObservableCollection 변경이 UI 스레드에서만 발생.
- `[ObservableProperty]`/`[RelayCommand]` 제너레이터로 INPC·커맨드 생성. `CanSend`/`CanStop`가 `[NotifyCanExecuteChangedFor]`로 갱신.
- XAML 바인딩 ↔ VM 멤버 전수 대조: MainWindow/ChatOnlyWindow(AgentSessionViewModel), SettingsWindow(SettingsViewModel), TranscriptTemplates(전 Transcript VM + ApprovalRequestViewModel) 전부 일치. 컨버터 리소스·DataTemplate 키·TranscriptItemTemplateSelector 배선 모두 정상.
- App.xaml.cs DI 배선이 명세 Part F 구성 순서와 정확히 일치. `OnExit` 비동기 제거·MCP 블록 제거 완료. SettingsViewModel에 `IAgentApiClient` 주입(GetModelsAsync용).

### Phase 0 클린업
- 삭제 대상 13개 파일 전부 디스크에서 제거 확인. 삭제 타입 참조 0건(AppSettings/SettingsService의 주석 처리된 McpPort/McpEnabled 언급은 마이그레이션 문서용 — 무해).
- `.csproj`에서 Microsoft.SemanticKernel PackageReference 제거 확인. 남은 참조: CommunityToolkit.Mvvm 8.3.2, Newtonsoft.Json 13.0.3, System.Drawing.Common 8.0.0.
- SettingsService v2→v3 마이그레이션: McpPort/McpEnabled는 Newtonsoft 역직렬화 시 자동 무시, 신규 필드 기본값 세팅 후 저장. 정상.

---

## 권고 사항 (블로커 아님 — Phase 2 후보)

- **R1 (보안, Medium):** `WorkspaceContext.ResolvePath`는 `Path.GetFullPath`로 `..`·절대경로 탈출을 막지만, **심볼릭 링크/정션(junction)** 은 해석하지 않는다. 워크스페이스 내부에 외부를 가리키는 정션이 존재하면 파일 도구가 그 정션을 통해 외부에 쓸 수 있다. 명세의 Phase 1 바인딩 계약(GetFullPath + StartsWith)은 충족하나, 강화하려면 해석 후 경로(`new DirectoryInfo(full).ResolveLinkTarget(true)` 또는 `File.ResolveLinkTarget`)를 재검증할 것을 Phase 2에 권고.
- **R2 (안전, Low):** `DeleteTool`/`MoveTool`이 워크스페이스 **루트 자체**(`path: "."` 또는 `""`)에 대한 삭제/이동을 별도로 막지 않는다. `ResolvePath("")`는 Root를 반환하므로 `delete`에 빈 경로가 오면 루트 삭제 시도가 가능(`recursive:true` 시 워크스페이스 전체 삭제). 루트 동일 경로일 때 거부하는 가드 추가를 권고.
- **R3 (정합성, Low):** 도구 내부에 개별 try/catch가 없고 IO 예외를 오케스트레이터 `ExecuteCallAsync`가 중앙에서 `ToolResult.Fail`로 변환한다. 요구사항(예외→is_error 변환, 크래시 아님)은 **충족**되나, 명세 C.6 문구("On exception return ToolResult.Fail")는 도구별 처리를 가정. 현재 중앙 처리도 유효하므로 변경 불필요 — 설계 의도 기록용.
- **R4 (정리, Low):** `Views/MessageTemplateSelector.cs`, `ViewModels/ChatMessageViewModel.cs`, `Models/UserMessagesDto.cs`/`AgentResponsesDto.cs`는 구 채팅 잔재(명세상 KEEP, harmless)로 현재 미사용. Phase 2에서 사용 여부 재확인 후 제거 검토.

---

## 사용자 판단 필요 항목

없음. Phase 1 범위 내 블로커 없음.

---

## R1~R4 반영 결과 (2026-06-18)

모두 반영 완료. 빌드 에러 0개(경고 2건 NU1510, 기존과 동일).

| 권고 | 처리 | 변경 파일 |
|------|------|-----------|
| R1 (보안) | `WorkspaceContext`에 `RealPath()` 추가 — 심볼릭/정션 링크 최종 대상을 해석해 `_realRoot` 기준 실제 경로 재검증. 미존재 경로는 존재하는 조상까지 해석 후 tail 결합. `ResolvePath`/`IsInsideWorkspace` 모두 2단(사전적+실제) 검증. | `Services/WorkspaceContext.cs` |
| R2 (안전) | `DeleteTool`·`MoveTool`에 워크스페이스 루트 자체 삭제/이동/덮어쓰기 차단 가드 추가(`path:"."`·`sub/..` 포함). | `Services/Tools/DeleteTool.cs`, `MoveTool.cs` |
| R3 (정합성) | 코드 동작 변경 없음(요구 충족). 중앙 예외처리 설계 의도를 `ExecuteCallAsync`에 주석 기록. | `Services/AgentOrchestrator.cs` |
| R4 (정리) | 미사용 채팅 잔재 제거: `MessageTemplateSelector`, `ChatMessageViewModel`, `UserMessagesDto`, `AgentResponsesDto`(참조 0 확인 후 삭제). | 4개 파일 삭제 |
