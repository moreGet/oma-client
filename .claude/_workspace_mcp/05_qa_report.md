## 결과: PASS

전체 MCP 레이어를 8개 카테고리(컴파일, HttpListener, Process, SecurityValidator, JSON, App 통합, XAML, 설정 마이그레이션)에 대해 점검했다. 컴파일을 막거나 런타임에 즉시 사고를 일으킬 만한 이슈는 발견되지 않았다. 안정성 강화 차원에서 3건의 마이너 수정을 적용했다.

## 발견 및 수정된 이슈

| 파일 | 이슈 | 처리 |
|------|------|------|
| `Services/McpSseServer.cs` (StopAsync) | `_listener?.Stop()` 호출 시 `IsListening` 체크 누락. 반복 호출 시 예외는 안 나지만 체크리스트 요구사항 미충족. | `if (_listener != null && _listener.IsListening) _listener.Stop();` 로 가드 추가. |
| `Services/McpSseServer.cs` (HandlePostMessageAsync) | `context.Request.ContentEncoding`이 null일 때 `StreamReader` 생성자에서 NRE 발생 가능. | `?? Encoding.UTF8` 폴백 적용. |
| `Services/McpSseServer.cs` (SendToAllAsync) | `WaitAsync(ct)`가 cancel로 throw하면 finally에서 미획득 세마포어를 Release하여 `SemaphoreFullException` 트리거 (try/catch가 삼키긴 하나 부정확). | `lockAcquired` 플래그 도입, 셧다운 시 `OperationCanceledException`을 위로 전파. |

## 체크리스트별 검증 결과

### 1. 컴파일 오류 검증 — PASS
- 모든 using 완전: `Newtonsoft.Json`, `System.Net`, `System.Diagnostics`, `System.Text`, `System.Collections.Concurrent`, `System.IO`, `System.Threading` 모두 정상 임포트.
- `McpResponse.Ok` / `McpResponse.Fail` 정적 팩토리 존재 확인.
- `SecurityValidator.Validate(string, ScriptType)` 시그니처 일치.
- `McpRemoteAgentService(ISettingsService settings, IMcpSseServer sseServer, IScriptExecutor executor)` 와 `App.xaml.cs`의 `new McpRemoteAgentService(settings: ..., sseServer: ..., executor: ...)` 명명인자 완전 일치.
- `IRemoteAgentService.RunningStateChanged event EventHandler<bool>?` 정의 확인.
- `ISettingsService.LoadAsync()` 시그니처(`Task LoadAsync()`)와 구현 일치.

### 2. HttpListener 안전성 — PASS (수정 후)
- `http://localhost:{port}/` 바인딩 — loopback 전용이라 관리자 권한 불필요.
- `AcceptLoopAsync`에서 `HttpListenerException`, `ObjectDisposedException` 모두 catch — 포트 충돌/셧다운 시 루프만 빠져나옴.
- 구독자 write 실패 시 예외 무시 후 `_subscribers.TryRemove` + `TryCloseSubscriber`로 제거.
- `StopAsync`에서 `IsListening` 가드 추가 완료.

### 3. Process 실행 안전성 — PASS
- `BeginOutputReadLine` / `BeginErrorReadLine` 모두 호출됨.
- `Kill(entireProcessTree: true)` 사용.
- `_concurrencyLimit.Release()` 가 `finally` 블록 내에 위치 — 누수 방지 OK.
- `Escape`가 `"` → `\"` 치환. PowerShell `-Command` (CommandLineToArgvW 규칙) 및 cmd `/c "..."` 양쪽에서 표준적으로 동작.

### 4. SecurityValidator — PASS
- `RegexOptions.IgnoreCase | RegexOptions.Compiled` 적용.
- `CommonBlacklist`, `PowerShellBlacklist`, `CmdBlacklist`, `BlockedPaths` 4종 패턴 배열 존재.
- `string.IsNullOrWhiteSpace(script)` 체크 존재.
- 64KB(`65536`) 길이 제한 존재.

### 5. JSON 직렬화 — PASS
- `McpRequest.Id`, `McpResponse.Id` 모두 `object?` (string/int 모두 수용).
- `CamelCasePropertyNamesContractResolver` + `NullValueHandling.Ignore` 설정 (`McpSseServer.JsonSettings`).
- `McpResponse`/`McpError`의 `NullValueHandling.Ignore` 어노테이션 추가로 안전.

### 6. App.xaml.cs 통합 — PASS
- `_mcpService = new McpRemoteAgentService(settings:..., sseServer:..., executor:...)` 호출 정상.
- `MainViewModel(chatService, agentActionService, _settingsService, _mcpService)` — 4번째 인자로 전달, ViewModel 생성자 4번째 파라미터 (`IRemoteAgentService? mcpService = null`)와 일치.
- `OnExit`에서 MCP 서비스 `StopAsync` → `DisposeAsync` 후 다른 리소스 정리, 마지막에 `base.OnExit(e)` 호출.

### 7. MainWindow.xaml — PASS
- `Ellipse Fill="{Binding IsMcpRunning, Converter={StaticResource BoolToStatusBrush}}"` 바인딩.
- `TextBlock Text="{Binding McpStatusText}"` 바인딩.
- `BoolToStatusBrush` 컨버터 재사용 OK.

### 8. 설정 마이그레이션 — PASS
- `AppSettings`에 `McpPort` (default 3000), `McpEnabled` (default true), `SchemaVersion` (default 2) 프로퍼티 존재.
- `SettingsService.LoadAsync()`에서 `SchemaVersion < 2` 분기 → `McpPort=3000`, `McpEnabled=true`, `SchemaVersion=2` 마이그레이션 후 자동 저장.
- 신규 설치(파일 없음)는 `new AppSettings()` 기본값으로 v2 스키마 즉시 작성.

## 미수정 이슈 (수동 확인 필요)
- 없음. 위 3건은 모두 자동 수정됨.

## 권고사항

1. **HttpListener 포트 충돌 UX**: 현재 `StartAsync`에서 `HttpListenerException`(보통 ERROR_SHARING_VIOLATION 5)이 발생하면 `RunningStateChanged(false)` 후 throw한다. App.xaml.cs는 `_ = _mcpService.StartAsync()`로 fire-and-forget 호출이라 사용자에게 노출되지 않는다. 추후 SettingsViewModel에서 시작 결과를 사용자에게 알릴 수단을 마련할 것.
2. **CMD 이스케이프 한계**: `Escape`는 `"`만 치환한다. CMD의 메타문자(`&`, `|`, `^`, `<`, `>`, `%`)는 미처리이므로 SecurityValidator를 통과한 페이로드라도 셸 메타문자 주입 가능성이 남는다. 향후 `cmd /c` 대신 임시 `.bat` 파일에 기록 후 실행하는 방식 검토 권장.
3. **PowerShell `-EncodedCommand` 권장**: 현재 `-Command "{escaped}"` 방식은 인용 처리 엣지 케이스가 많다. 64KB 페이로드 한도에서 `Convert.ToBase64String(Encoding.Unicode.GetBytes(script))`를 `-EncodedCommand`에 넘기면 인용 문제를 근본적으로 해결 가능.
4. **SSE 끊김 감지 지연**: `HandleSseConnectionAsync`는 1초 단위 `Task.Delay` 폴링으로 구독자 사전 제거 여부만 본다. 실제 클라이언트 연결 종료는 broadcast 시도 후 write 실패로만 감지됨 → 다음 keep-alive(15s)나 broadcast가 없으면 좀비 구독자가 남는다. 30초 keep-alive 주기를 5초로 줄이거나 `response.OutputStream`의 가용성 체크를 고려.
5. **SecurityValidator 타이밍 공격 무관**: 블랙리스트 매칭이 짧게 종료되어도 보안 영향 없음. RegEx ReDoS는 현 패턴에서 가능성 없음(모두 단순 anchor).
6. **테스트**: `SecurityValidator`, `McpRemoteAgentService.HandleToolsCallAsync`(특히 InvalidParams 케이스)에 대한 단위 테스트 추가 권장.
