# MCP Server 레이어 서비스 요약 (02_service_summary)

**작성일**: 2026-04-26
**구현 단계**: ServiceEngineer
**전제 명세**: `01_architect_spec.md`

---

## 1. 생성/수정 파일 목록

### 1.1 신규 (Models/Mcp)

| 파일 | 클래스 | 비고 |
|------|--------|------|
| `Models/Mcp/ScriptType.cs` | `enum ScriptType { PowerShell, Cmd }` | - |
| `Models/Mcp/McpError.cs` | `McpError` | JSON-RPC 표준 + 커스텀 코드 상수 (`ParseError`, `InvalidRequest`, `MethodNotFound`, `InvalidParams`, `InternalError`, `SecurityViolation`, `ExecutionFailed`, `ExecutionTimeout`) |
| `Models/Mcp/McpRequest.cs` | `McpRequest` | `JsonRpc`, `Id` (object?), `Method`, `Params` (Dictionary<string,object?>?) |
| `Models/Mcp/McpResponse.cs` | `McpResponse` | `Ok(id, result)`, `Fail(id, code, message, data?)` 정적 팩토리 + `NullValueHandling.Ignore` |
| `Models/Mcp/McpTool.cs` | `McpTool` | `Name`, `Description`, `InputSchema (object)` |
| `Models/Mcp/ScriptResult.cs` | `ScriptResult` | `Stdout`, `Stderr`, `ExitCode`, `Success (computed)`, `DurationMs`, `TimedOut` |
| `Models/Mcp/ValidationResult.cs` | `ValidationResult` | `IsValid (init)`, `Reason`, `MatchedPattern` + `Valid()` / `Invalid(reason, pattern?)` |

### 1.2 신규 (Services)

| 파일 | 타입 | 비고 |
|------|------|------|
| `Services/SecurityValidator.cs` | `static class` | `Validate(script, ScriptType)`. 정적 readonly `(Regex, string)[]` 4종 (Common / PowerShell / Cmd / BlockedPaths). `RegexOptions.IgnoreCase \| RegexOptions.Compiled` |
| `Services/IScriptExecutor.cs` | interface | `ExecutePowerShellAsync`, `ExecuteCmdAsync` |
| `Services/ScriptExecutor.cs` | class | `Process` 래퍼. `SemaphoreSlim(4,4)` 동시성 제한. `BeginOutputReadLine`/`BeginErrorReadLine`. 타임아웃 시 `Kill(entireProcessTree:true)`. 인자 escape `"` -> `\"` |
| `Services/IMcpSseServer.cs` | interface : IAsyncDisposable | `IsListening`, `Port`, `RequestHandler`, `StartAsync`, `StopAsync`, `BroadcastAsync` |
| `Services/McpSseServer.cs` | class | `HttpListener` + `ConcurrentDictionary<Guid, Subscriber>`. `GET /sse`, `POST /message`, `OPTIONS *`. 15초 keep-alive `PeriodicTimer`. 구독자별 `SemaphoreSlim` 으로 stream write 직렬화 |
| `Services/IRemoteAgentService.cs` | interface : IAsyncDisposable | `IsRunning`, `Port`, `RunningStateChanged` 이벤트, `StartAsync`, `StopAsync` |
| `Services/McpRemoteAgentService.cs` | class | 진입 서비스. JSON-RPC 메서드 라우터 (`initialize`, `tools/list`, `tools/call`, `ping`). `ISettingsService` + `IMcpSseServer` + `IScriptExecutor` 조합 |

### 1.3 수정

- `Models/AppSettings.cs`: `SchemaVersion = 2` 로 상향, `int McpPort = 3000`, `bool McpEnabled = true` 프로퍼티 2개 추가.
- `Services/ISettingsService.cs`: 변경 없음 (현재 단계에서는 마이그레이션 헬퍼 불필요).
- `Services/SettingsService.cs`: 변경 없음 (역직렬화 시 신규 필드는 기본값으로 초기화되므로 자연 마이그레이션 가능. 명시적 `version<2` 분기는 후속 단계에서 추가).

---

## 2. 주요 구현 포인트

### 2.1 SecurityValidator
- 모든 패턴은 `RegexOptions.IgnoreCase | RegexOptions.Compiled` 로 정적 초기화 -> 첫 호출에서만 컴파일 비용 발생.
- 검증 흐름: empty -> length(>64KB) -> CommonBlacklist -> typed blacklist -> BlockedPaths.
- `CmdBlacklist` 는 의도적으로 비어 있고 CommonBlacklist 만 적용 (스펙대로).
- `Reason` 은 한국어 사용자용, `MatchedPattern` 은 디버깅용 RegEx 원문.

### 2.2 ScriptExecutor
- 동시 실행 한도 `SemaphoreSlim(4,4)` (기본 4 슬롯). `WaitAsync(ct)` 로 진입.
- stdout/stderr 비동기 캡처(`BeginOutputReadLine`/`BeginErrorReadLine`) 로 deadlock 방지.
- 타임아웃: `CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(timeoutMs)`. 외부 ct 취소와 timeout 을 `OperationCanceledException` 캐치 시 `!ct.IsCancellationRequested` 로 구분.
- 타임아웃 시: `Process.Kill(entireProcessTree: true)` -> `ScriptResult { ExitCode=-1, TimedOut=true }`.
- PowerShell 인자: `-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "..."`.
- CMD 인자: `/c "..."`. 양쪽 모두 `"` -> `\"` 단순 escape.
- `StandardOutputEncoding/StandardErrorEncoding = Encoding.UTF8` 로 한글 깨짐 방지.

### 2.3 McpSseServer
- 바인딩: `http://localhost:{port}/` (loopback 예외, 관리자 권한 불필요).
- 라우팅:
  - `GET /sse`: `Content-Type: text/event-stream`, 최초 `event: endpoint\ndata: /message\n\n` 전송 후 구독자 등록. 1초 cooperative wait 루프로 연결 유지.
  - `POST /message`: 본문을 `McpRequest` 로 역직렬화 -> 즉시 `202 Accepted` HTTP 응답 -> `RequestHandler` 비동기 호출 -> 결과를 `BroadcastAsync` 로 SSE push.
  - `OPTIONS *`: CORS preflight (`Access-Control-Allow-Origin: *`).
  - 그 외: 404.
- keep-alive: 15초 `PeriodicTimer` 로 `: keep-alive\n\n` 전송. write 실패 시 해당 구독자 제거.
- 구독자별 `SemaphoreSlim(1,1)` 로 stream write 직렬화 -> keep-alive vs broadcast 동시 write 충돌 방지.
- `BroadcastAsync` 는 snapshot 패턴(ToArray) 으로 lock-free.
- `StopAsync`: ct 취소 -> 모든 구독자 close -> `_listener.Stop()` -> `_listener.Close()` -> 백그라운드 루프 await.
- JSON 직렬화: `CamelCasePropertyNamesContractResolver` + `NullValueHandling.Ignore` 정적 인스턴스.

### 2.4 McpRemoteAgentService
- `Tools` 배열은 정적 readonly. `inputSchema` 는 익명 객체로 정의 (직렬화 시 camelCase 자동 변환).
- 메서드 라우팅: `initialize` / `tools/list` / `tools/call` / `ping` / 기타(`MethodNotFound`).
- `tools/call` 흐름: `name` 추출 -> `arguments` 추출 -> `SecurityValidator.Validate` -> 통과 시 `IScriptExecutor` 호출 -> 결과를 `{ content: [{type:"text", text}], isError, exitCode, durationMs }` 형태로 변환.
- 에러 매핑:
  - 검증 실패 -> `SecurityViolation (-32000)`
  - Process 시작 예외 -> `ExecutionFailed (-32001)`
  - 타임아웃 -> `ExecutionTimeout (-32002)`
- params 추출 헬퍼는 `JValue` / `JObject` (Newtonsoft) 와 일반 `IDictionary` 양쪽 모두 처리.

### 2.5 동시성 / 안전성
- `RequestHandler` 동시 호출 가능 (SSE 서버가 fire-and-forget Task 로 디스패치).
- `ScriptExecutor` 는 stateless + Semaphore 로 안전.
- `_subscribers` 는 `ConcurrentDictionary` 로 lock-free 추가/제거.
- 모든 `*Async` 메서드는 `ConfigureAwait(false)`.

---

## 3. App.xaml.cs 통합 가이드 (생성자 시그니처)

DI 컴포지션 루트에서 다음 순서로 생성한다:

```csharp
// 기존 _settingsService 가 ISettingsService 인스턴스라고 가정
IMcpSseServer  sseServer      = new McpSseServer();           // 인자 없음
IScriptExecutor scriptExecutor = new ScriptExecutor();         // 인자 없음
IRemoteAgentService mcpService = new McpRemoteAgentService(
    settings:  _settingsService,
    sseServer: sseServer,
    executor:  scriptExecutor);

if (_settingsService.Current.McpEnabled)
{
    _ = mcpService.StartAsync(); // fire-and-forget
}
```

`OnExit` 에서:

```csharp
protected override async void OnExit(ExitEventArgs e)
{
    try
    {
        if (_mcpService != null)
        {
            await _mcpService.StopAsync();
            await _mcpService.DisposeAsync();
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"MCP shutdown error: {ex}");
    }
    base.OnExit(e);
}
```

### 생성자 시그니처 정리

| 타입 | 생성자 |
|------|--------|
| `McpSseServer` | `McpSseServer()` (파라미터 없음) |
| `ScriptExecutor` | `ScriptExecutor()` (파라미터 없음) |
| `McpRemoteAgentService` | `McpRemoteAgentService(ISettingsService settings, IMcpSseServer sseServer, IScriptExecutor executor)` |

### 의존성 인터페이스

- `ISettingsService` (기존, `Current.McpPort`, `Current.McpEnabled` 사용)
- `IMcpSseServer` (신규, `McpSseServer` 구현체)
- `IScriptExecutor` (신규, `ScriptExecutor` 구현체)

### ViewModel 주입 (선택)

`MainViewModel` 에서 `IRemoteAgentService` 를 주입받아 `RunningStateChanged` 이벤트로 상태바 텍스트 갱신:

```csharp
mcpService.RunningStateChanged += (_, running) =>
{
    McpStatusText = running ? $"MCP :{mcpService.Port}" : "MCP off";
};
```

---

## 4. QA 체크리스트 인계 사항

- [x] Models/Mcp 7개 파일 모두 `OhMyAgent.AiAgent.Client.Models.Mcp` 네임스페이스
- [x] `IRemoteAgentService` / `IMcpSseServer` 모두 `IAsyncDisposable` 구현
- [x] `McpRequest.Id` 가 `object?` 타입 (string/int/null round-trip 가능)
- [x] `SecurityValidator` 패턴이 명세서 5.1 ~ 5.3 모두 반영
- [x] `ScriptExecutor` 가 타임아웃 시 `Kill(entireProcessTree: true)` 호출
- [x] `McpSseServer` 가 다중 구독자 환경에서 한 구독자 끊김에 영향 없음 (snapshot 패턴 + 실패 시 제거)
- [x] CamelCase JSON 직렬화 (`CamelCasePropertyNamesContractResolver`)
- [x] `AppSettings.SchemaVersion = 2` 로 상향
- [ ] (후속) `App.xaml.cs` `OnExit` 가 MCP 서비스 정리 후 base 호출 — UI 단계에서 작업
- [ ] (후속) 포트 충돌 시 UI 알림 — ViewModel 단계에서 작업
- [ ] (후속) `SettingsService` 마이그레이션 분기 (`version<2` 명시 처리) — 필요 시 후속 작업

---

**END OF SUMMARY**
