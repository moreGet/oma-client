# MCP Server 레이어 설계 명세 (01_architect_spec)

**대상 프로젝트**: `OhMyAgent.AiAgent.Client` (.NET 10.0, WPF, MVVM)
**작성일**: 2026-04-26
**범위**: 중앙 Go 서버(Agent Host)와 SSE + JSON-RPC 2.0 통신을 수행하는 MCP Server 레이어 신규 구축

---

## 1. 신규 파일 목록

### 1.1 Models/Mcp/ (신규 디렉토리)

| 경로 | 클래스 | 역할 |
|------|--------|------|
| `Models/Mcp/McpRequest.cs` | `McpRequest` | JSON-RPC 2.0 Request DTO. `JsonRpc`, `Id`, `Method`, `Params` 필드 |
| `Models/Mcp/McpResponse.cs` | `McpResponse` | JSON-RPC 2.0 Response DTO. `JsonRpc`, `Id`, `Result`, `Error` 필드 |
| `Models/Mcp/McpError.cs` | `McpError` | JSON-RPC 2.0 Error 객체. `Code`, `Message`, `Data` 필드 + 표준 에러 코드 상수 |
| `Models/Mcp/McpTool.cs` | `McpTool` | MCP Tool 정의 DTO. `Name`, `Description`, `InputSchema` 필드 |
| `Models/Mcp/ScriptResult.cs` | `ScriptResult` | 스크립트 실행 결과. `Stdout`, `Stderr`, `ExitCode`, `Success`, `DurationMs` |
| `Models/Mcp/ValidationResult.cs` | `ValidationResult` | 보안 검증 결과. `IsValid`, `Reason`, `MatchedPattern` |
| `Models/Mcp/ScriptType.cs` | `ScriptType` (enum) | `PowerShell`, `Cmd` 구분 |

**네임스페이스**: `OhMyAgent.AiAgent.Client.Models.Mcp`

### 1.2 Services/ (기존 디렉토리에 추가)

| 경로 | 클래스/인터페이스 | 역할 |
|------|------------------|------|
| `Services/IRemoteAgentService.cs` | `IRemoteAgentService` | MCP 레이어 진입 인터페이스. `IAsyncDisposable` 구현 |
| `Services/McpRemoteAgentService.cs` | `McpRemoteAgentService` | `IRemoteAgentService` 구현. SSE 서버 + JSON-RPC 라우팅 호스팅 |
| `Services/IScriptExecutor.cs` | `IScriptExecutor` | 스크립트 실행 인터페이스 |
| `Services/ScriptExecutor.cs` | `ScriptExecutor` | `System.Diagnostics.Process` 기반 PowerShell/CMD 실행기 |
| `Services/SecurityValidator.cs` | `SecurityValidator` (정적) | 블랙리스트 RegEx 기반 사전 검증 |
| `Services/IMcpSseServer.cs` | `IMcpSseServer` | HTTP SSE 서버 인터페이스 |
| `Services/McpSseServer.cs` | `McpSseServer` | `HttpListener` 기반 SSE + `POST /message` 처리 |

**네임스페이스**: `OhMyAgent.AiAgent.Client.Services`

---

## 2. 수정 파일 목록

| 경로 | 변경 내용 |
|------|----------|
| `Models/AppSettings.cs` | `int McpPort { get; set; } = 3000;`, `bool McpEnabled { get; set; } = true;` 프로퍼티 2개 추가. `SchemaVersion` 1 -> 2로 증가 (마이그레이션 트리거) |
| `Services/ISettingsService.cs` | (선택) `Task UpdateMcpPortAsync(int port)` 메서드 추가. 기존 `UpdateXxxAsync` 패턴 따름 |
| `Services/SettingsService.cs` | (선택) `UpdateMcpPortAsync` 구현. `SchemaVersion` 마이그레이션 분기 추가 |
| `App.xaml.cs` | `_mcpService` 필드 추가, `OnStartup`에서 생성 + 조건부 `StartAsync()`, `OnExit`에서 `await StopAsync()` |
| `ViewModels/MainViewModel.cs` | (선택) `bool IsMcpRunning`, `string McpStatusText` 프로퍼티 + `IRemoteAgentService` 주입 |
| `Views/MainWindow.xaml` | (선택) 상태바에 `McpStatusText` 바인딩 추가 |

---

## 3. 인터페이스 계약 (전체 시그니처)

### 3.1 Models/Mcp

```csharp
// Models/Mcp/McpRequest.cs
namespace OhMyAgent.AiAgent.Client.Models.Mcp;

public class McpRequest
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonProperty("id")]
    public object? Id { get; set; }            // string | number | null

    [JsonProperty("method")]
    public string Method { get; set; } = string.Empty;

    [JsonProperty("params")]
    public Dictionary<string, object?>? Params { get; set; }
}

// Models/Mcp/McpResponse.cs
public class McpResponse
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonProperty("id")]
    public object? Id { get; set; }

    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
    public object? Result { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public McpError? Error { get; set; }

    public static McpResponse Ok(object? id, object? result)
        => new() { Id = id, Result = result };

    public static McpResponse Fail(object? id, int code, string message, object? data = null)
        => new() { Id = id, Error = new McpError { Code = code, Message = message, Data = data } };
}

// Models/Mcp/McpError.cs
public class McpError
{
    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public object? Data { get; set; }

    // 표준 JSON-RPC 2.0
    public const int ParseError      = -32700;
    public const int InvalidRequest  = -32600;
    public const int MethodNotFound  = -32601;
    public const int InvalidParams   = -32602;
    public const int InternalError   = -32603;

    // 커스텀 (Anthropic MCP 권장 범위 -32000 ~ -32099)
    public const int SecurityViolation = -32000;
    public const int ExecutionFailed   = -32001;
    public const int ExecutionTimeout  = -32002;
}

// Models/Mcp/McpTool.cs
public class McpTool
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("inputSchema")]
    public object InputSchema { get; set; } = new { };
}

// Models/Mcp/ScriptResult.cs
public class ScriptResult
{
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public bool Success => ExitCode == 0;
    public long DurationMs { get; set; }
    public bool TimedOut { get; set; }
}

// Models/Mcp/ValidationResult.cs
public class ValidationResult
{
    public bool IsValid { get; init; }
    public string? Reason { get; init; }
    public string? MatchedPattern { get; init; }

    public static ValidationResult Valid() => new() { IsValid = true };
    public static ValidationResult Invalid(string reason, string? pattern = null)
        => new() { IsValid = false, Reason = reason, MatchedPattern = pattern };
}

// Models/Mcp/ScriptType.cs
public enum ScriptType { PowerShell, Cmd }
```

### 3.2 Services 인터페이스

```csharp
// Services/IRemoteAgentService.cs
public interface IRemoteAgentService : IAsyncDisposable
{
    bool IsRunning { get; }
    int Port { get; }
    event EventHandler<bool>? RunningStateChanged;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}

// Services/IMcpSseServer.cs
public interface IMcpSseServer : IAsyncDisposable
{
    bool IsListening { get; }
    int Port { get; }

    /// <summary>JSON-RPC 요청 처리 핸들러. McpRemoteAgentService가 등록.</summary>
    Func<McpRequest, CancellationToken, Task<McpResponse>>? RequestHandler { get; set; }

    Task StartAsync(int port, CancellationToken ct = default);
    Task StopAsync();
    Task BroadcastAsync(McpResponse response, CancellationToken ct = default);
}

// Services/IScriptExecutor.cs
public interface IScriptExecutor
{
    Task<ScriptResult> ExecutePowerShellAsync(string script, int timeoutMs = 30000, CancellationToken ct = default);
    Task<ScriptResult> ExecuteCmdAsync(string command, int timeoutMs = 30000, CancellationToken ct = default);
}

// Services/SecurityValidator.cs
public static class SecurityValidator
{
    public static ValidationResult Validate(string script, ScriptType scriptType);
}
```

### 3.3 표준 MCP 에러 코드 매핑

| 코드 | 상수 | 사용 상황 |
|------|------|----------|
| -32700 | `ParseError` | JSON 파싱 실패 |
| -32600 | `InvalidRequest` | `jsonrpc != "2.0"` 또는 필수 필드 누락 |
| -32601 | `MethodNotFound` | 미지원 메서드 호출 |
| -32602 | `InvalidParams` | 파라미터 타입/개수 오류 |
| -32603 | `InternalError` | 핸들러 예외 |
| -32000 | `SecurityViolation` | 블랙리스트 매칭 |
| -32001 | `ExecutionFailed` | 프로세스 비정상 종료 (ExitCode != 0 외) |
| -32002 | `ExecutionTimeout` | 타임아웃으로 Process.Kill() |

---

## 4. MCP SSE Server 통신 프로토콜

### 4.1 엔드포인트

| 메서드 | 경로 | 설명 |
|-------|------|------|
| `GET` | `/sse` | 클라이언트가 SSE 연결 수립. 서버는 Content-Type: `text/event-stream` 으로 응답 후 keep-alive 루프 진입 |
| `POST` | `/message` | 클라이언트가 JSON-RPC 요청 본문 전송. HTTP 응답은 `202 Accepted` (빈 본문). 실제 결과는 SSE 채널로 push |
| `OPTIONS` | `/*` | CORS preflight (`Access-Control-Allow-Origin: *`) |

**바인딩 주소**: `http://localhost:{port}/` (loopback 예외로 관리자 권한 불필요)

### 4.2 SSE 응답 형식

```
HTTP/1.1 200 OK
Content-Type: text/event-stream
Cache-Control: no-cache
Connection: keep-alive
Access-Control-Allow-Origin: *

event: endpoint
data: /message

event: message
data: {"jsonrpc":"2.0","id":"1","result":{...}}

: keep-alive
```

- 각 이벤트 끝은 `\n\n` (빈 줄)
- 15초 간격 keep-alive 주석 (`: keep-alive\n\n`) 으로 연결 유지
- 클라이언트가 끊으면 `WriteAsync` 시 `IOException` 발생 -> 구독자 목록에서 제거

### 4.3 지원 MCP 메서드

| 메서드 | params | result |
|-------|--------|--------|
| `initialize` | `{ protocolVersion, capabilities, clientInfo }` | `{ protocolVersion: "2024-11-05", capabilities: { tools: {} }, serverInfo: { name: "OhMyAgent", version: "1.0.0" } }` |
| `tools/list` | `{}` | `{ tools: McpTool[] }` |
| `tools/call` | `{ name: string, arguments: { script?: string, command?: string, timeoutMs?: number } }` | `{ content: [{ type: "text", text: stdout }], isError: bool }` |
| `ping` | `{}` | `{}` |

### 4.4 등록 Tool 목록

```jsonc
[
  {
    "name": "run_powershell",
    "description": "Execute a PowerShell script on the local Windows machine. Subject to security validation.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "script":    { "type": "string",  "description": "PowerShell script body" },
        "timeoutMs": { "type": "integer", "default": 30000 }
      },
      "required": ["script"]
    }
  },
  {
    "name": "run_cmd",
    "description": "Execute a Windows CMD command. Subject to security validation.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "command":   { "type": "string" },
        "timeoutMs": { "type": "integer", "default": 30000 }
      },
      "required": ["command"]
    }
  }
]
```

---

## 5. SecurityValidator 블랙리스트 규칙

### 5.1 공통 RegEx 패턴 (대소문자 무시)

| 패턴 | 차단 사유 |
|------|----------|
| `\brmdir\s+/s\b` | 재귀 디렉토리 삭제 |
| `\brd\s+/s\b` | 재귀 디렉토리 삭제 |
| `\bformat\s+[a-z]:` | 디스크 포맷 |
| `\bdel\s+/[fqs]` | 강제 삭제 |
| `\berase\s+/[fqs]` | 강제 삭제 |
| `\breg\s+(delete|add)\b.*HKLM\\\\SYSTEM` | 시스템 레지스트리 변조 |
| `\bshutdown\b` | 시스템 종료 |

### 5.2 PowerShell 전용 패턴

| 패턴 | 차단 사유 |
|------|----------|
| `Stop-Computer` | 시스템 종료 |
| `Restart-Computer` | 재부팅 |
| `Remove-Item\b.*-Recurse\b.*-Force\b` | 재귀 강제 삭제 |
| `Remove-Item\b.*-Force\b.*-Recurse\b` | 재귀 강제 삭제 (옵션 순서 반대) |
| `\bInvoke-Expression\b` | 동적 코드 실행 |
| `\biex\s` | `Invoke-Expression` 별칭 |
| `\bSet-ExecutionPolicy\b` | 실행 정책 변조 |

### 5.3 차단 디렉토리 (경로 인자)

- `C:\\Windows\\System32`
- `C:\\Windows\\SysWOW64`
- `C:\\Program Files`
- `C:\\Program Files \(x86\)`
- `C:\\ProgramData`
- `%SystemRoot%`
- `%WinDir%`

### 5.4 선택 차단 (네트워크) - 기본 비활성, 향후 설정화

- `\bDownloadFile\b`
- `\bWebClient\b`
- `\bInvoke-WebRequest\b`
- `\bcurl\.exe\b`, `\bwget\.exe\b`

### 5.5 검증 흐름

```csharp
public static ValidationResult Validate(string script, ScriptType scriptType)
{
    // 1. null/empty 체크
    if (string.IsNullOrWhiteSpace(script))
        return ValidationResult.Invalid("Empty script");

    // 2. 길이 제한 (예: 64KB)
    if (script.Length > 65536)
        return ValidationResult.Invalid("Script exceeds 64KB");

    // 3. 공통 패턴 매칭
    foreach (var (regex, reason) in CommonBlacklist)
        if (regex.IsMatch(script))
            return ValidationResult.Invalid(reason, regex.ToString());

    // 4. 타입별 패턴 매칭
    var typed = scriptType == ScriptType.PowerShell ? PowerShellBlacklist : CmdBlacklist;
    foreach (var (regex, reason) in typed)
        if (regex.IsMatch(script))
            return ValidationResult.Invalid(reason, regex.ToString());

    // 5. 차단 디렉토리 매칭
    foreach (var (regex, reason) in BlockedPaths)
        if (regex.IsMatch(script))
            return ValidationResult.Invalid(reason, regex.ToString());

    return ValidationResult.Valid();
}
```

---

## 6. 데이터 흐름

```
[중앙 Go 서버 (Agent Host)]
    | (1) GET /sse  -> SSE 연결 수립
    | (2) POST /message {jsonrpc, id, method, params}
    v
[McpSseServer (HttpListener)]
    | HandlePostMessageAsync
    |   - JSON 역직렬화 -> McpRequest
    |   - HTTP 202 즉시 응답
    |   - RequestHandler 비동기 호출
    v
[McpRemoteAgentService.HandleRequestAsync]
    | switch (request.Method)
    |   case "initialize"  -> McpResponse.Ok(serverInfo)
    |   case "tools/list"  -> McpResponse.Ok({tools: [...]})
    |   case "tools/call"  -> CallToolAsync(name, args)
    |       |
    |       v
    |   [CallToolAsync]
    |       | ScriptType 결정 (run_powershell | run_cmd)
    |       | SecurityValidator.Validate(script, type)
    |       |   FAIL -> McpResponse.Fail(SecurityViolation, reason)
    |       |   PASS
    |       |       v
    |       |   [ScriptExecutor.ExecutePowerShellAsync / ExecuteCmdAsync]
    |       |       | Process.Start(...) + stdout/stderr 비동기 캡처
    |       |       | Timeout -> Process.Kill() + ExecutionTimeout
    |       |       | ScriptResult 반환
    |       |       v
    |       |   McpResponse.Ok({
    |       |     content: [{type:"text", text: result.Stdout}],
    |       |     isError: !result.Success
    |       |   })
    v
[McpSseServer.BroadcastAsync(response)]
    | 모든 SSE 구독자에게 "event: message\ndata: {...}\n\n" push
    v
[중앙 Go 서버] - SSE 이벤트 수신
```

---

## 7. App.xaml.cs 수정 내용

### 7.1 필드 추가

```csharp
private IRemoteAgentService? _mcpService;
```

### 7.2 OnStartup 추가 (기존 DI 컴포지션 루트 마지막에)

```csharp
// MCP Server (loopback, no admin required)
_mcpService = new McpRemoteAgentService(
    settingsService: _settingsService!,
    sseServer:       new McpSseServer(),
    scriptExecutor:  new ScriptExecutor());

if (_settingsService!.Current.McpEnabled)
{
    // fire-and-forget; 내부에서 예외 로깅 후 IsRunning=false 유지
    _ = _mcpService.StartAsync();
}
```

### 7.3 OnExit 추가

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
        // log only, do not block shutdown
        System.Diagnostics.Debug.WriteLine($"MCP shutdown error: {ex}");
    }
    base.OnExit(e);
}
```

### 7.4 ViewModel 주입 (선택)

```csharp
var mainVm = new MainViewModel(_settingsService!, _mcpService);
```

---

## 8. 구현 주의사항

### 8.1 HttpListener
- `http://localhost:{port}/` 또는 `http://127.0.0.1:{port}/` 는 loopback 예외로 관리자 권한 불필요
- `http://+:{port}/`, `http://*:{port}/` 는 `netsh http add urlacl` 필요 -> **사용 금지**
- `Prefixes.Add` 시 마지막 `/` 필수
- 포트 충돌 시 `HttpListenerException` -> 로깅 후 `IsRunning=false` 유지, UI에 에러 알림

### 8.2 SSE 연결 관리
- 구독자: `ConcurrentDictionary<Guid, HttpListenerResponse>`
- keep-alive 타이머: `PeriodicTimer(TimeSpan.FromSeconds(15))` 로 `: keep-alive\n\n` 전송
- `WriteAsync` 실패 시 해당 구독자 제거 + `response.Close()` (예외 무시)
- `StopAsync` 에서 모든 구독자 순회하며 close

### 8.3 ScriptExecutor 구현 가이드

```csharp
// PowerShell
ProcessStartInfo psi = new()
{
    FileName  = "powershell.exe",
    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{Escape(script)}\"",
    RedirectStandardOutput = true,
    RedirectStandardError  = true,
    UseShellExecute = false,
    CreateNoWindow  = true,
    StandardOutputEncoding = Encoding.UTF8,
    StandardErrorEncoding  = Encoding.UTF8,
};

// CMD
psi.FileName  = "cmd.exe";
psi.Arguments = $"/c \"{command}\"";
```

- stdout/stderr 캡처: `Process.OutputDataReceived` + `BeginOutputReadLine` (deadlock 방지)
- 타임아웃: `Task.WhenAny(process.WaitForExitAsync(ct), Task.Delay(timeoutMs, ct))`
- 타임아웃 시: `process.Kill(entireProcessTree: true)` -> `TimedOut=true`, `ExitCode=-1`
- CancellationToken 연동: `ct.Register(() => process.Kill())`

### 8.4 JSON 직렬화
- 기존 의존성 `Newtonsoft.Json 13.0.3` 사용
- 공통 `JsonSerializerSettings` 정적 인스턴스:
  ```csharp
  new JsonSerializerSettings
  {
      ContractResolver = new CamelCasePropertyNamesContractResolver(),
      NullValueHandling = NullValueHandling.Ignore,
      DateFormatHandling = DateFormatHandling.IsoDateFormat
  }
  ```
- `Id` 필드는 `object?` -> string/int 모두 round-trip 가능

### 8.5 동시성
- `RequestHandler` 는 동시 호출 가능. `ScriptExecutor` 는 stateless 라 안전
- 동시 실행 프로세스 수 제한 (예: `SemaphoreSlim(maxConcurrency: 4)`)
- `BroadcastAsync` 는 구독자 목록 순회 시 lock 또는 snapshot 패턴

### 8.6 로깅
- 현시점 별도 로깅 추상화 없으므로 `System.Diagnostics.Debug.WriteLine` 사용
- 향후 `ILogger` 도입 시 일괄 교체 가능하도록 단일 진입점 (`McpRemoteAgentService.Log`) 통과

### 8.7 설정 마이그레이션
- `AppSettings.SchemaVersion` 1 -> 2
- `SettingsService.LoadAsync` 에서 `version < 2` 면 `McpPort=3000`, `McpEnabled=true` 기본값 주입 후 저장

### 8.8 보안 추가 권고
- `SecurityValidator` 통과해도 `tools/call` 호출자 ID/origin 검증 필요 (향후 Bearer 토큰)
- 실행 사용자 권한 명시: 현재 프로세스 권한으로 실행됨 -> 앱을 관리자로 띄우면 위험도 상승
- stdout/stderr 크기 제한 (예: 1MB) 후 truncate 표시

---

## 9. 구현 순서 (다음 단계 가이드)

1. **Models/Mcp** 7개 파일 (DTO 만 -> 컴파일 가능 단위)
2. **SecurityValidator** + 단위 테스트 (정적, 의존성 없음)
3. **ScriptExecutor** + `IScriptExecutor` (Process 래퍼, 단독 테스트 가능)
4. **McpSseServer** + `IMcpSseServer` (HttpListener 단독 테스트 가능)
5. **McpRemoteAgentService** + `IRemoteAgentService` (위 3개 조합)
6. **AppSettings** 수정 + 마이그레이션
7. **App.xaml.cs** 컴포지션 루트 통합
8. (선택) **MainViewModel / MainWindow.xaml** 상태바 바인딩

---

## 10. 검증 체크리스트 (QA 인계용)

- [ ] `Models/Mcp` 7개 파일 모두 `OhMyAgent.AiAgent.Client.Models.Mcp` 네임스페이스 사용
- [ ] 모든 신규 인터페이스가 `IAsyncDisposable` 또는 `IDisposable` 적절히 구현
- [ ] `McpRequest.Id` 가 string/int/null 모두 round-trip
- [ ] `SecurityValidator` 가 모든 블랙리스트 패턴에 대해 단위 테스트 통과
- [ ] `ScriptExecutor` 가 타임아웃 시 자식 프로세스까지 종료
- [ ] `McpSseServer` 가 다중 구독자 환경에서 한 구독자 끊김에 영향 안 받음
- [ ] `App.xaml.cs` `OnExit` 가 MCP 서비스 정리 후 base 호출
- [ ] `AppSettings.SchemaVersion` 마이그레이션 동작
- [ ] 포트 충돌 시 앱이 크래시하지 않고 UI 에 알림
- [ ] CamelCase JSON 직렬화 (모든 필드 lowercase first)

---

**END OF SPEC**
