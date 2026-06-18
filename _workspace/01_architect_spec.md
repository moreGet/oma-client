# 01 — Architect Spec: Phase 0 (Cleanup) + Phase 1 (Agent Loop)

> Target: `net10.0-windows`, `Nullable=enable`, `ImplicitUsings=enable`, CommunityToolkit.Mvvm 8.x source generators.
> Namespace convention: `OhMyAgent.AiAgent.Client.{Layer}`.
> Paths below are relative to the **inner project dir**: `OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client/`.
> JSON: server DTOs use `System.Text.Json` (new code). Existing `Newtonsoft.Json` stays for settings persistence only — do NOT mix on the same type.

## 기능 명세 (one-line)
Transform the WPF chat client into a Claude-Code-style autonomous agent: an SSE tool-calling API client + an agent loop orchestrator that executes a registry of 11 sandboxed Windows tools inside a user-chosen workspace, gated by a Manual permission service, surfaced through a transcript ViewModel.

---

## PART A — PHASE 0: CODE CLEANUP

### A.1 Files to DELETE (8 files)

| # | File (under inner project dir) | Type removed |
|---|--------------------------------|--------------|
| 1 | `Services/AgentActionService.cs` | `AgentActionService` + `FileCreationPlugin` (SemanticKernel) |
| 2 | `Services/IAgentActionService.cs` | `IAgentActionService` |
| 3 | `Services/McpSseServer.cs` | `McpSseServer` |
| 4 | `Services/IMcpSseServer.cs` | `IMcpSseServer` |
| 5 | `Services/McpRemoteAgentService.cs` | `McpRemoteAgentService` |
| 6 | `Services/IRemoteAgentService.cs` | `IRemoteAgentService` |
| 7 | `Models/Mcp/McpRequest.cs` | `McpRequest` |
| 8 | `Models/Mcp/McpResponse.cs` | `McpResponse` |
| 9 | `Models/Mcp/McpError.cs` | `McpError` |
| 10 | `Models/Mcp/McpTool.cs` | `McpTool` |

> NOTE: `ChatService.cs` / `IChatService.cs` are **refactored, not deleted** (see A.5). They MAY be deleted if you instead create `AgentApiClient.cs` / `IAgentApiClient.cs` as new files and remove the old ones — recommended path is **create new + delete old** so MainViewModel's old streaming surface vanishes cleanly. Decision: **DELETE `ChatService.cs` + `IChatService.cs`**, CREATE `AgentApiClient.cs` + `IAgentApiClient.cs`.

So total Phase 0 deletions = 10 MCP/Action files **+ `Services/ChatService.cs` + `Services/IChatService.cs`** = 12 files.

### A.2 Files to KEEP & REUSE (do NOT touch in Phase 0 except where noted)

| File | Status |
|------|--------|
| `Services/ScriptExecutor.cs`, `Services/IScriptExecutor.cs` | KEEP — becomes `run_command` engine |
| `Services/SecurityValidator.cs` | KEEP — extended later (Phase 2); used by `run_command` now |
| `Models/Mcp/ScriptResult.cs` | KEEP — folder name `Mcp` is now a misnomer but leave it to avoid churn; do NOT move |
| `Models/Mcp/ScriptType.cs` | KEEP |
| `Models/Mcp/ValidationResult.cs` | KEEP |
| `Services/SettingsService.cs`, `Services/ISettingsService.cs` | KEEP + EXTEND (A.6) |
| `Services/GlobalHotkeyService.cs` (+ I), `TrayNotificationService.cs` (+ I), `ChatWindowCoordinator.cs` (+ I) | KEEP — tray/hotkey/floating window |
| `Services/AgentException.cs` | KEEP — reused for API client failures |
| `Views/ChatOnlyWindow.*`, `Views/SettingsWindow.*`, `Views/Converters.cs`, `Views/MessageTemplateSelector.cs` | KEEP (Settings extended in Part E) |
| `Resources/Colors.xaml`, `Converters.xaml`, `Styles.xaml` (dark theme) | KEEP |
| `ViewModels/ChatMessageViewModel.cs`, `SettingsViewModel.cs` | KEEP |
| `Models/AppSettings.cs` | KEEP + EXTEND (A.6) |
| `Models/HotkeySettings.cs`, `HotkeyModifiers.cs`, `DomainOptions.cs` | KEEP |
| `Models/UserMessagesDto.cs`, `AgentResponsesDto.cs` | KEEP (legacy chat DTOs — harmless; may be deleted later if unused) |

### A.3 `.csproj` edit — remove SemanticKernel

`OhMyAgent.AiAgent.Client.csproj`, delete lines 17–18:
```xml
        <!-- Semantic Kernel (로컬 에이전트 액션) -->
        <PackageReference Include="Microsoft.SemanticKernel" Version="1.14.1" />
```
Keep CommunityToolkit.Mvvm, Newtonsoft.Json, System.Drawing.Common. (System.Text.Json ships with the SDK — no PackageReference needed.)

### A.4 `App.xaml.cs` edits — remove all deleted-type references

`App.xaml.cs`. Apply these exact removals/replacements (full DI rewrite is in Part F; Phase 0 minimal version below):

- **Line 28**: DELETE `private IRemoteAgentService? _mcpService;`
- **Lines 47–48**: REPLACE
  ```csharp
  var chatService        = new ChatService(_httpClient);
  var agentActionService = new AgentActionService();
  ```
  → with the new wiring from Part F (constructs `WorkspaceContext`, `ScriptExecutor`, tools, `ToolRegistry`, `PermissionService`, `AgentApiClient`, `AgentOrchestrator`).
- **Lines 50–54**: DELETE the entire `_mcpService = new McpRemoteAgentService(...)` block.
- **Line 57**: REPLACE
  ```csharp
  _mainVm = new MainViewModel(chatService, agentActionService, _settingsService, _mcpService);
  ```
  → new `AgentSessionViewModel` construction (Part F). MainViewModel is retired (see D.4).
- **Lines 88–90**: DELETE the MCP startup block:
  ```csharp
  if (_settingsService!.Current.McpEnabled)
      _ = _mcpService.StartAsync();
  ```
- **Lines 112–116**: DELETE the MCP shutdown block inside `OnExit`:
  ```csharp
  if (_mcpService != null)
  {
      await _mcpService.StopAsync();
      await _mcpService.DisposeAsync();
  }
  ```
  After removal `OnExit` keeps `_globalHotkey?.Dispose(); _trayIcon?.Dispose(); _httpClient?.Dispose();`. The `try/catch` wrapper can be dropped (nothing throws now) — keep `OnExit` non-async if no awaits remain, but leaving `async` is harmless. **Decision:** make `OnExit` synchronous (remove `async`), since no awaited cleanup remains.
- The `MainWindow` field type (`_mainWindow`) is unaffected. `InitializeTrayIcon`, `CreateAppIcon`, hotkey wiring (lines 74–83, 93–106), `RegisterMainWindowHwnd` are UNCHANGED.

### A.5 `MainViewModel.cs` — RETIRE, replaced by `AgentSessionViewModel`

The string-parsing `[ACTION:CREATE_FILE]` hack and the whole single-shot chat loop are gone. **Decision: delete `MainViewModel.cs`** and replace with `AgentSessionViewModel` (Part D). All references to the deleted types live only in `MainViewModel.cs` (lines 12–15, 55–72, 108–118, 175, 189–193) and `App.xaml.cs` — both are rewritten. After Part D + Part F there are **zero** dangling references to: `IChatService`, `ChatService`, `IAgentActionService`, `AgentActionService`, `IRemoteAgentService`, `McpRemoteAgentService`, `McpSseServer`, `IMcpSseServer`, `McpRequest/Response/Error/Tool`, `Microsoft.SemanticKernel`.

> `MainWindow(_mainVm)` constructor param type changes `MainViewModel` → `AgentSessionViewModel`; update `MainWindow.xaml.cs` ctor + `MainWindow.xaml` `d:DataContext` (UIDesigner). `ChatWindowCoordinator` references the VM via `Func<MainViewModel>` — change its generic to `AgentSessionViewModel` (it only needs the VM for window coordination; verify what members it touches and keep them or stub them on the new VM).

### A.6 `AppSettings` + `SettingsService` extension

`Models/AppSettings.cs` — ADD fields (preserve all existing), and REMOVE the now-orphaned MCP fields:
```csharp
public class AppSettings
{
    // existing — KEEP
    public HotkeySettings Hotkey { get; set; } = HotkeySettings.Default;
    public double Opacity { get; set; } = 1.0;
    public int SchemaVersion { get; set; } = 3;   // bump 2 -> 3

    // REMOVE (MCP server retired):
    //   public int  McpPort    { get; set; } = 3000;
    //   public bool McpEnabled { get; set; } = true;

    // NEW (Phase 1)
    public string WorkspaceRoot { get; set; } = "";            // empty => prompt user / default to Desktop
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Manual;
    public int MaxIterations { get; set; } = 25;
    public string ServerBaseUrl { get; set; } = "http://localhost:8080";
    public string AuthScheme { get; set; } = "Bearer";          // "Bearer" | "ApiKey"
    public string AuthToken { get; set; } = "";
    public string ModelId { get; set; } = "corp-llm-32b";
    public int MaxTokens { get; set; } = 4096;
}
```
`SettingsService.cs` — schema migration v2→v3: drop `McpPort`/`McpEnabled`, default the new fields. Add async updaters mirroring the existing pattern:
```csharp
Task UpdateWorkspaceRootAsync(string path);
Task UpdatePermissionModeAsync(PermissionMode mode);
Task UpdateServerConfigAsync(string baseUrl, string scheme, string token, string modelId, int maxIterations, int maxTokens);
```
Keep existing `UpdateHotkeyAsync`, `UpdateOpacityAsync`, `LoadAsync`, `SaveAsync`, `Current`, `SettingsChanged`. `ISettingsService` gains the new method signatures + `Current` already exposes the new props.

> `App.xaml.cs` line 38 `BaseAddress = new Uri("http://localhost:8080")` → after settings load, set `_httpClient.BaseAddress = new Uri(_settingsService.Current.ServerBaseUrl)` (Part F).

---

## PART B — MODELS LAYER (all NEW unless noted)

All under `Models/` (or `Models/Agent/` subfolder — **Decision: `Models/Agent/`** to keep the new domain grouped). Namespace `OhMyAgent.AiAgent.Client.Models`. Use `System.Text.Json.Serialization.JsonPropertyName` for wire DTOs. Records are `sealed` and immutable.

### B.1 Enums — `Models/Agent/AgentEnums.cs`
```csharp
public enum MessageRole { System, User, Assistant, Tool }

public enum ToolRisk { ReadOnly, Write, Execute, Destructive }

public enum PermissionMode { Manual, AutoSafe, FullAuto }

public enum PermissionDecision { Allow, Deny, AlwaysAllow }

public enum StopReason { ToolUse, EndTurn, MaxTokens, Error, Unknown }
```
> Wire `stop_reason` strings (`"tool_use"`,`"end_turn"`,`"max_tokens"`,`"error"`) are parsed to `StopReason` by `AgentApiClient`; `MessageStop.StopReason` is kept as the raw `string` (per API_CONTRACT §6) for forward-compat, orchestrator maps it.

### B.2 Conversation / request DTOs — `Models/Agent/AgentMessage.cs`
```csharp
// One conversation turn. Serialized into AgentRequest.Messages.
public sealed record AgentMessage
{
    [JsonPropertyName("role")]    public required MessageRole Role { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }

    // assistant turns only
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    // tool turns only
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; init; }   // tool turns only

    // factory helpers (no logic in model otherwise)
    public static AgentMessage System(string content);
    public static AgentMessage User(string content);
    public static AgentMessage Assistant(string? content, IReadOnlyList<ToolCall>? toolCalls = null);
    public static AgentMessage ToolResultMsg(string toolCallId, string content, bool isError);
}
```
> `MessageRole` must serialize lower-case (`system/user/assistant/tool`). Provide a `JsonStringEnumConverter` with naming policy = snake/lower, OR a custom converter. **Decision:** register `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)` in the shared `JsonSerializerOptions` (ServiceEngineer owns a `static AgentJson.Options`). Same converter handles `StopReason` if ever serialized.

### B.3 `Models/Agent/ToolCall.cs`
```csharp
public sealed record ToolCall(
    [property: JsonPropertyName("id")]        string Id,
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("arguments")] JsonElement Arguments);
```
> `Arguments` is the raw JSON object the model produced; tools parse it. `JsonElement` round-trips fine through System.Text.Json.

### B.4 `Models/Agent/ToolSchema.cs`
```csharp
public sealed record ToolSchema(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")]  JsonElement Parameters);  // JSON Schema object
```

### B.5 `Models/Agent/RequestMetadata.cs`
```csharp
public sealed record RequestMetadata(
    [property: JsonPropertyName("os")]             string Os,            // "windows"
    [property: JsonPropertyName("workspace")]      string Workspace,     // resolved root
    [property: JsonPropertyName("client_version")] string ClientVersion);
```

### B.6 `Models/Agent/AgentRequest.cs`
```csharp
public sealed record AgentRequest(
    [property: JsonPropertyName("model")]      string Model,
    [property: JsonPropertyName("stream")]     bool Stream,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")]   IReadOnlyList<AgentMessage> Messages,
    [property: JsonPropertyName("tools")]      IReadOnlyList<ToolSchema> Tools,
    [property: JsonPropertyName("metadata")]   RequestMetadata Metadata);
```

### B.7 `Models/Agent/Usage.cs` + `ModelInfo.cs`
```csharp
public sealed record Usage(
    [property: JsonPropertyName("input_tokens")]  int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);

public sealed record ModelInfo(
    [property: JsonPropertyName("id")]              string Id,
    [property: JsonPropertyName("display_name")]    string DisplayName,
    [property: JsonPropertyName("supports_tools")]  bool SupportsTools,
    [property: JsonPropertyName("supports_vision")] bool SupportsVision);
```

### B.8 SSE stream events — `Models/Agent/AgentStreamEvent.cs`
Wire-facing; emitted by `IAgentApiClient`. (Per API_CONTRACT §6, signatures fixed.)
```csharp
public abstract record AgentStreamEvent;
public sealed record MessageStart(string Id, string Model)                    : AgentStreamEvent;
public sealed record ContentDelta(string Text)                               : AgentStreamEvent;
public sealed record ToolCallEvent(string Id, string Name, JsonElement Args) : AgentStreamEvent;
public sealed record MessageStop(string StopReason, Usage Usage)             : AgentStreamEvent;
public sealed record ErrorEvent(string Code, string Message)                 : AgentStreamEvent;
```

### B.9 Tool execution support — `Models/Agent/ToolResult.cs`, `ToolContext.cs`
```csharp
// Returned by ITool.ExecuteAsync. Content is the string that becomes the `tool` message content.
public sealed record ToolResult(string Content, bool IsError)
{
    public static ToolResult Ok(string content)    => new(content, false);
    public static ToolResult Fail(string message)  => new(message, true);
    // Convenience: serialize a structured object to JSON content
    public static ToolResult Json(object payload)  => new(JsonSerializer.Serialize(payload, AgentJson.Options), false);
}

// Passed to every tool: ambient execution context (no DI inside tools).
public sealed record ToolContext(
    IWorkspaceContext Workspace,
    PermissionMode PermissionMode);
```
> `ToolContext` lives in Models but references `IWorkspaceContext` (Services). To keep MVVM purity (Model knows no layer), **Decision: define `ToolContext` in `Services/` namespace** (`OhMyAgent.AiAgent.Client.Services`) since it carries a service reference, OR define `IWorkspaceContext` in Models. Cleanest: put `ITool`, `IToolRegistry`, `ToolContext` together in Services (tools are services). `ToolResult`, `ToolCall`, `ToolSchema`, `ToolRisk` stay in Models (pure data). **Final: `ToolContext` -> Services layer.**

### B.10 Orchestrator output events — `Models/Agent/AgentEvent.cs`
Emitted by `IAgentOrchestrator.RunAsync` to the ViewModel. UI-facing, richer than wire events.
```csharp
public abstract record AgentEvent;
public sealed record AgentTextDelta(string Text)                              : AgentEvent;  // assistant prose token
public sealed record AgentAssistantMessageComplete(string Text)              : AgentEvent;  // a full assistant turn closed
public sealed record AgentToolCallStarted(string CallId, string ToolName, JsonElement Args, ToolRisk Risk) : AgentEvent;
public sealed record AgentAwaitingApproval(string CallId, string ToolName, JsonElement Args, ToolRisk Risk) : AgentEvent;
public sealed record AgentToolCallResult(string CallId, string ToolName, ToolResult Result) : AgentEvent;
public sealed record AgentIterationAdvanced(int Iteration, int MaxIterations) : AgentEvent;
public sealed record AgentDone(string FinalText, Usage? LastUsage)           : AgentEvent;
public sealed record AgentError(string Code, string Message)                 : AgentEvent;
```

---

## PART C — SERVICES LAYER (signatures only; bodies = ServiceEngineer)

All under `Services/`. Namespace `OhMyAgent.AiAgent.Client.Services`. CancellationToken last. `IAsyncEnumerable` for streams.

### C.1 `IAgentApiClient` / `AgentApiClient` (refactor of ChatService)
Files: `Services/IAgentApiClient.cs`, `Services/AgentApiClient.cs`.
```csharp
public interface IAgentApiClient
{
    // POST /api/v1/agent/chat, stream:true -> parse SSE per API_CONTRACT §4.2.
    IAsyncEnumerable<AgentStreamEvent> SendAsync(AgentRequest request, CancellationToken ct = default);

    // GET /api/v1/health -> 200 with {status:"ok"}.
    Task<bool> CheckHealthAsync(CancellationToken ct = default);

    // GET /api/v1/models -> ModelInfo[]. Empty list if endpoint absent.
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default);
}

public sealed class AgentApiClient : IAgentApiClient
{
    public AgentApiClient(HttpClient httpClient, ISettingsService settings);
    // ctor reads settings.Current for Authorization header (AuthScheme/AuthToken) per request.
}
```
Implementation notes for ServiceEngineer (binding contract):
- Serialize `AgentRequest` with `AgentJson.Options` (System.Text.Json). Body `Content-Type: application/json`; `Accept: text/event-stream`.
- Auth header per `settings.Current.AuthScheme`: `Bearer` → `Authorization: Bearer {token}`; `ApiKey` → `X-Api-Key: {token}`. Skip if token empty.
- SSE parsing: read line-by-line. Buffer `event: <name>` then `data: <json>`; dispatch on blank line. Map:
  `message_start`→`MessageStart`, `content_delta`→`ContentDelta`, `tool_call`→`ToolCallEvent` (parse `arguments` to `JsonElement`), `message_stop`→`MessageStop`, `error`→`ErrorEvent`.
- On HTTP non-2xx, parse `{error:{code,message}}` and yield a single `ErrorEvent`, OR throw `AgentException` (Decision: yield `ErrorEvent` so the loop can surface it; throw `AgentException` only on transport failure / unreachable host).
- Reuse `AgentException` for connection failures.

### C.2 `IToolRegistry` / `ToolRegistry` + `ITool`
Files: `Services/ITool.cs`, `Services/IToolRegistry.cs`, `Services/ToolRegistry.cs`.
```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }   // JSON Schema object (the tool's `parameters`)
    ToolRisk Risk { get; }
    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default);
}

public interface IToolRegistry
{
    IReadOnlyList<ITool> All { get; }
    IReadOnlyList<ToolSchema> ToSchemas();          // -> AgentRequest.Tools
    bool TryGet(string name, out ITool tool);
}

public sealed class ToolRegistry : IToolRegistry
{
    public ToolRegistry(IEnumerable<ITool> tools);  // constructed with the 11 MVP tools
}
```
> API_CONTRACT §6 declares `JsonSchema ParametersSchema`. There is no built-in `JsonSchema` type; **Decision: use `JsonElement`** (a parsed JSON Schema object). `ToSchemas()` maps each tool → `new ToolSchema(Name, Description, ParametersSchema)`.

### C.3 `IWorkspaceContext` / `WorkspaceContext`
Files: `Services/IWorkspaceContext.cs`, `Services/WorkspaceContext.cs`.
```csharp
public interface IWorkspaceContext
{
    string Root { get; }                                   // absolute, normalized
    string ResolvePath(string relativeOrAbsolute);          // throws if escapes workspace
    bool IsInsideWorkspace(string path);
    void SetRoot(string root);                              // called when settings change
}

public sealed class WorkspaceContext : IWorkspaceContext
{
    public WorkspaceContext(ISettingsService settings);    // seeds Root from settings.Current.WorkspaceRoot
}
```
Binding contract (ServiceEngineer):
- `ResolvePath`: combine with `Root` if relative, `Path.GetFullPath` to normalize, reject if `!IsInsideWorkspace(result)` → throw `AgentException("경로가 작업 디렉토리를 벗어났습니다: {path}")`. Block `..` escape and absolute paths pointing outside Root.
- `IsInsideWorkspace`: case-insensitive `StartsWith(Root + Path.DirectorySeparatorChar)` after `GetFullPath`.
- Empty `Root` fallback: `Environment.GetFolderPath(SpecialFolder.DesktopDirectory)`.

### C.4 `IPermissionService` / `PermissionService`
Files: `Services/IPermissionService.cs`, `Services/PermissionService.cs`.
```csharp
public interface IPermissionService
{
    // Returns the decision for a tool call. May await UI (Manual mode) via the approval handler.
    Task<PermissionDecision> RequestAsync(ToolCall call, ToolRisk risk, ToolContext ctx, CancellationToken ct = default);

    // ViewModel registers a callback that surfaces the inline approval card and returns the user's choice.
    void SetApprovalHandler(Func<ToolCall, ToolRisk, CancellationToken, Task<PermissionDecision>> handler);

    void ClearSessionRules();   // forget AlwaysAllow grants (e.g., on new session)
}

public sealed class PermissionService : IPermissionService
{
    public PermissionService(ISettingsService settings);   // reads Current.PermissionMode at decision time
}
```
Decision logic (binding contract):
- Mode `FullAuto` → always `Allow` (SecurityValidator blacklist still applies inside `run_command`).
- Mode `AutoSafe` → `ReadOnly` auto-`Allow`; `Write`/`Execute`/`Destructive` → consult session AlwaysAllow set, else invoke approval handler.
- Mode `Manual` (default) → `ReadOnly` still auto-`Allow` (reads are safe & noisy to approve — **Decision: ReadOnly auto-allowed in Manual too**; only Write/Execute/Destructive gate). Everything else → AlwaysAllow set, else handler.
- AlwaysAllow storage: `HashSet<string>` keyed by **tool name** (session-scoped, in-memory). `PermissionDecision.AlwaysAllow` adds `call.Name` to the set; subsequent same-name calls auto-`Allow`. (Per-argument granularity deferred to Phase 2.)
- If no handler registered (headless), default `Deny` for gated risks.

### C.5 `IAgentOrchestrator` / `AgentOrchestrator` (the loop)
Files: `Services/IAgentOrchestrator.cs`, `Services/AgentOrchestrator.cs`.
```csharp
public interface IAgentOrchestrator
{
    // Runs one full goal to completion (or Stop). Emits AgentEvent stream to the VM.
    IAsyncEnumerable<AgentEvent> RunAsync(string userGoal, AgentSession session, CancellationToken ct = default);
}

public sealed class AgentOrchestrator : IAgentOrchestrator
{
    public AgentOrchestrator(
        IAgentApiClient   api,
        IToolRegistry     tools,
        IPermissionService permissions,
        IWorkspaceContext  workspace,
        ISettingsService   settings);
}
```
**`AgentSession`** (state object the client holds — stateless server). File `Services/AgentSession.cs` (or Models; **Decision: Services** since it is mutable runtime state):
```csharp
public sealed class AgentSession
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public List<AgentMessage> Messages { get; } = new();  // full history incl. system prompt
    public Usage? LastUsage { get; set; }
    public static string DefaultSystemPrompt(string workspaceRoot, PermissionMode mode);
}
```
Loop algorithm (binding contract for ServiceEngineer, per PLAN §3 + CONTRACT §4):
1. If session empty, prepend `AgentMessage.System(AgentSession.DefaultSystemPrompt(...))`.
2. Append `AgentMessage.User(userGoal)`.
3. `iteration = 0`. Loop while `iteration < settings.Current.MaxIterations` and `!ct.IsCancellationRequested`:
   a. Emit `AgentIterationAdvanced(iteration+1, max)`.
   b. Build `AgentRequest(model, stream:true, maxTokens, session.Messages, tools.ToSchemas(), metadata{os:"windows", workspace:workspace.Root, client_version})`.
   c. `await foreach` `api.SendAsync(req, ct)`:
      - `ContentDelta` → emit `AgentTextDelta`; accumulate assistant text.
      - `ToolCallEvent` → collect into a pending list (do NOT execute mid-stream).
      - `MessageStop` → record `StopReason` + `Usage` (session.LastUsage); break inner loop.
      - `ErrorEvent` → emit `AgentError`; return.
   d. Append `AgentMessage.Assistant(accumulatedText, pendingToolCalls)` (pendingToolCalls may be null) to `session.Messages`. Emit `AgentAssistantMessageComplete`.
   e. If `StopReason != tool_use` (i.e., `end_turn`/`max_tokens`/none) → emit `AgentDone(accumulatedText, lastUsage)`; **return** (loop ends).
   f. For each pending `ToolCall`:
      - resolve `ITool` via `tools.TryGet(name)`; if missing → append `AgentMessage.ToolResultMsg(id, "Unknown tool: {name}", isError:true)`, emit `AgentToolCallResult` with error, continue.
      - `risk = tool.Risk`. Emit `AgentToolCallStarted(id, name, args, risk)`.
      - `decision = await permissions.RequestAsync(call, risk, ctx, ct)` — before calling, emit `AgentAwaitingApproval` if the decision will block on the UI handler (PermissionService raises that via handler; orchestrator emits `AgentAwaitingApproval` right before awaiting). **Decision: orchestrator emits `AgentAwaitingApproval` whenever risk is gated and mode≠FullAuto, just before awaiting RequestAsync.**
      - If `Deny` → append `ToolResultMsg(id, "Denied by user", isError:true)`; emit `AgentToolCallResult` error; continue.
      - Else build `ToolContext(workspace, settings.Current.PermissionMode)`, `result = await tool.ExecuteAsync(args, ctx, ct)`.
      - Append `AgentMessage.ToolResultMsg(id, result.Content, result.IsError)`; emit `AgentToolCallResult(id, name, result)`.
   g. `iteration++`; loop back to (a) → resend with appended tool results.
4. If loop exits on `iteration == max` → emit `AgentError("max_iterations", "최대 반복 횟수에 도달했습니다.")`.
5. On `OperationCanceledException` → emit `AgentError("cancelled", "사용자가 중지했습니다.")` and stop (do not rethrow out of the enumerable).

### C.6 The 11 MVP tools (each `ITool`)
Folder: `Services/Tools/`. Each is `sealed class {Name}Tool : ITool`. `ParametersSchema` is a `static readonly JsonElement` parsed once from a JSON Schema string (helper `ToolSchemas.Parse(string json)`). All file-path params go through `ctx.Workspace.ResolvePath(...)`. On exception return `ToolResult.Fail(ex.Message)`.

| Class / file | `Name` | `Risk` | `parameters` (JSON Schema `properties`, `required`) | Behavior |
|---|---|---|---|---|
| `RunCommandTool` | `run_command` | `Execute` | `shell`:enum[`powershell`,`cmd`], `command`:string. required:[shell,command] | Map `shell`→`ScriptType`; `SecurityValidator.Validate(command, type)` → if invalid `ToolResult.Fail(reason)`; else `ScriptExecutor.ExecutePowerShell/CmdAsync` with `WorkingDirectory=ctx.Workspace.Root`. Return `ToolResult.Json(new{exit_code,stdout,stderr})`, `IsError = exitCode!=0 || timedOut`. (ScriptExecutor currently lacks a workingDir param — see note ★.) |
| `ReadFileTool` | `read_file` | `ReadOnly` | `path`:string, `start_line`:int?, `end_line`:int?. required:[path] | Resolve path; read file (UTF-8); optional 1-based inclusive line slice. Return content (cap ~200KB, note truncation). |
| `WriteFileTool` | `write_file` | `Write` | `path`:string, `content`:string. required:[path,content] | Resolve; create parent dirs; overwrite UTF-8. Return `{path, bytes_written}`. **Replaces AgentActionService.** |
| `EditFileTool` | `edit_file` | `Write` | `path`:string, `old_string`:string, `new_string`:string, `replace_all`:bool?. required:[path,old_string,new_string] | Resolve; read; require `old_string` unique unless `replace_all`; replace; write. Fail if not found / ambiguous. Return `{path, replacements}`. |
| `ListDirectoryTool` | `list_directory` | `ReadOnly` | `path`:string?. (default workspace root) | Resolve (root if null); list entries `{name,type:file|dir,size}`. Return JSON array. |
| `GlobTool` | `glob` | `ReadOnly` | `pattern`:string (e.g. `**/*.cs`), `path`:string?. required:[pattern] | Glob under base (root or path), workspace-bounded. Return `{files:[...]}` (relative paths). |
| `GrepTool` | `grep` | `ReadOnly` | `pattern`:string (regex), `path`:string?, `glob`:string?, `ignore_case`:bool?. required:[pattern] | Regex search across matching files. Return `{matches:[{file,line,text}]}` (cap N). |
| `CreateDirectoryTool` | `create_directory` | `Write` | `path`:string. required:[path] | Resolve; `Directory.CreateDirectory`. Return `{path}`. |
| `MoveTool` | `move` | `Destructive` | `source`:string, `destination`:string, `overwrite`:bool?. required:[source,destination] | Resolve both (must be inside workspace); move file/dir. Return `{source,destination}`. |
| `CopyTool` | `copy` | `Destructive` | `source`:string, `destination`:string, `overwrite`:bool?. required:[source,destination] | Resolve both; copy file or recursive dir. Return `{source,destination}`. |
| `DeleteTool` | `delete` | `Destructive` | `path`:string, `recursive`:bool?. required:[path] | Resolve; delete file/dir (recursive if set). Return `{path,deleted:true}`. |

> ★ ScriptExecutor working-directory: current `ExecutePowerShellAsync/ExecuteCmdAsync(script, timeoutMs, ct)` has no working-dir param. **Decision: add an optional `string? workingDirectory = null` parameter** to both methods + `IScriptExecutor` (set `ProcessStartInfo.WorkingDirectory`). This is the only allowed edit to the reused executor in Phase 1; default null preserves existing behavior. `RunCommandTool` passes `ctx.Workspace.Root`.

> JSON Schema authoring: ServiceEngineer writes each `parameters` schema as a const string and parses with `JsonSerializer.Deserialize<JsonElement>(json, AgentJson.Options)`. Example for `run_command`:
> ```json
> {"type":"object","properties":{"shell":{"type":"string","enum":["powershell","cmd"]},"command":{"type":"string"}},"required":["shell","command"]}
> ```

### C.7 Shared JSON options — `Services/AgentJson.cs`
```csharp
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
```

---

## PART D — VIEWMODELS LAYER

All under `ViewModels/`. CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). Namespace `OhMyAgent.AiAgent.Client.ViewModels`.

### D.1 `AgentSessionViewModel` (NEW — replaces MainViewModel)
File: `ViewModels/AgentSessionViewModel.cs`.
Constructor:
```csharp
public AgentSessionViewModel(
    IAgentOrchestrator orchestrator,
    IAgentApiClient    api,
    IPermissionService permissions,
    IWorkspaceContext  workspace,
    ISettingsService   settings);
```
At ctor: register the approval handler →
```csharp
permissions.SetApprovalHandler((call, risk, ct) => RequestApprovalAsync(call, risk, ct));
```
Observable properties:
| Property | Type | Notes |
|---|---|---|
| `InputText` | string | `[NotifyCanExecuteChangedFor(nameof(SendCommand))]` |
| `IsBusy` | bool | loop running; `[NotifyCanExecuteChangedFor(SendCommand, StopCommand)]` |
| `IsConnected` | bool | health |
| `HasError`, `ErrorMessage` | bool/string | |
| `StatusText` | string | "연결 중..."/"Connected"/"실행 중 ({iteration}/{max})" |
| `WorkspaceRoot` | string | mirrors settings; display in header |
| `CurrentPermissionMode` | PermissionMode | bound to a selector; setter calls `settings.UpdatePermissionModeAsync` |
| `WindowOpacity` | double | preserved from old VM (`OnWindowOpacityChanged` → `UpdateOpacityAsync`) |
| `PendingApproval` | `ApprovalRequestViewModel?` | non-null while an inline approval card is shown |
| `LastUsageText` | string | "in:1200 out:80" |

Collections:
```csharp
public ObservableCollection<ITranscriptItem> Transcript { get; } = [];
```
Commands:
| Command | Signature | Behavior |
|---|---|---|
| `SendCommand` | `async Task SendAsync(CancellationToken)` `CanExecute=CanSend` (`!IsBusy && !blank`) | snapshot InputText; add `UserTurnViewModel`; create CTS; `IsBusy=true`; `await foreach` `orchestrator.RunAsync(goal, _session, _cts.Token)` and project each `AgentEvent` onto the transcript (see D.5); `finally IsBusy=false`. |
| `StopCommand` | `void Stop()` `CanExecute=IsBusy` | `_cts?.Cancel()`. |
| `RetryConnectionCommand` | `async Task` | `IsConnected = await api.CheckHealthAsync()`. |
| `ClearCommand` | `void Clear()` | `Transcript.Clear(); _session = new AgentSession(); permissions.ClearSessionRules();` |
| `PickWorkspaceCommand` | `void` (raises a request to View for folder dialog) OR handled in Settings | see Part E. |

Approval surfacing (`RequestApprovalAsync`) — binding contract:
```csharp
private async Task<PermissionDecision> RequestApprovalAsync(ToolCall call, ToolRisk risk, CancellationToken ct)
{
    var vm = new ApprovalRequestViewModel(call.Name, risk, RenderArgs(call.Arguments));
    PendingApproval = vm;                       // View shows inline approval card bound to PendingApproval
    try   { return await vm.WaitForDecisionAsync(ct); }   // TaskCompletionSource awaited
    finally { PendingApproval = null; }
}
```
Must marshal to UI thread (orchestrator runs off-thread). All `Transcript`/property mutations go through `Dispatcher`/`ObservableObject` — ServiceEngineer's orchestrator is thread-free; **ViewModel is responsible for `Application.Current.Dispatcher.Invoke` when projecting events.** (Decision: VM marshals.)

`InitializeAsync()`: seed `WorkspaceRoot`/`CurrentPermissionMode`/`WindowOpacity` from settings; `await RetryConnection`; if connected add a `SystemNoticeViewModel` greeting.

### D.2 Transcript item VMs — `ViewModels/Transcript/`
```csharp
public interface ITranscriptItem { }   // marker; DataTemplateSelector keys off concrete type

public sealed partial class UserTurnViewModel    : ObservableObject, ITranscriptItem { public string Text {get;init;} }
public sealed partial class AssistantTurnViewModel : ObservableObject, ITranscriptItem
{
    [ObservableProperty] private string _text = "";      // streamed via AgentTextDelta append
    [ObservableProperty] private bool _isStreaming = true;
}
public sealed partial class SystemNoticeViewModel : ObservableObject, ITranscriptItem { public string Text {get;init;} }
public sealed partial class ToolCallViewModel : ObservableObject, ITranscriptItem
{
    public string CallId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public ToolRisk Risk { get; init; }
    public string ArgsPreview { get; init; } = "";          // pretty JSON
    [ObservableProperty] private ToolCallStatus _status = ToolCallStatus.Running; // Running|AwaitingApproval|Succeeded|Failed|Denied
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isExpanded;          // collapsible card
}
public enum ToolCallStatus { Running, AwaitingApproval, Succeeded, Failed, Denied }
```

### D.3 `ApprovalRequestViewModel` — `ViewModels/ApprovalRequestViewModel.cs`
```csharp
public sealed partial class ApprovalRequestViewModel : ObservableObject
{
    public string ToolName { get; }
    public ToolRisk Risk { get; }
    public string ArgsPreview { get; }
    public ApprovalRequestViewModel(string toolName, ToolRisk risk, string argsPreview);

    [RelayCommand] private void Allow();        // sets result Allow
    [RelayCommand] private void Deny();         // sets result Deny
    [RelayCommand] private void AlwaysAllow();  // sets result AlwaysAllow

    public Task<PermissionDecision> WaitForDecisionAsync(CancellationToken ct); // TaskCompletionSource
}
```

### D.4 Event→Transcript projection (binding contract for ViewModelEngineer)
In `SendAsync`, switch on each `AgentEvent` (all on Dispatcher):
- `AgentTextDelta`: ensure a trailing `AssistantTurnViewModel` exists; append to its `Text`.
- `AgentAssistantMessageComplete`: mark current assistant `IsStreaming=false`.
- `AgentToolCallStarted`: add a `ToolCallViewModel{Status=Running}`; index by `CallId`.
- `AgentAwaitingApproval`: set that item's `Status=AwaitingApproval`. (PendingApproval card already shown by handler.)
- `AgentToolCallResult`: find by `CallId`; set `ResultText=Result.Content`, `IsError=Result.IsError`, `Status = IsError? Failed : Succeeded` (Denied if content=="Denied by user").
- `AgentIterationAdvanced`: update `StatusText="실행 중 ({i}/{max})"`.
- `AgentDone`: finalize; `StatusText="완료"`; set `LastUsageText`.
- `AgentError`: `HasError=true; ErrorMessage=message`; append `SystemNoticeViewModel`.

### D.5 Relationship to `MainViewModel`
`MainViewModel` is **deleted** (A.5). `AgentSessionViewModel` is the new root VM bound to `MainWindow` and `ChatOnlyWindow`. Keep `WindowOpacity` behavior so the floating-window opacity feature still works. `SettingsViewModel` is extended in Part E. No coexistence — clean replacement.

---

## PART E — VIEWS LAYER (UIDesigner)

Keep: dark theme (`Resources/*.xaml`), floating `ChatOnlyWindow`, tray, hotkey. Only DataContext type changes (`MainViewModel`→`AgentSessionViewModel`) and new templates/controls.

### E.1 `MainWindow.xaml` (MODIFY) — transcript view
- `DataContext` → `AgentSessionViewModel`; ctor `MainWindow(AgentSessionViewModel vm)`.
- Transcript `ItemsControl`/`ListBox` bound to `Transcript` with a `TranscriptItemTemplateSelector` (new, `Views/TranscriptItemTemplateSelector.cs`) choosing DataTemplate per item type:
  - User/Assistant/System bubbles (reuse existing chat bubble styles).
  - **Tool call card** (collapsible): header = risk badge + tool name + status spinner/checkmark/✗; `Expander`/toggle bound to `IsExpanded` revealing `ArgsPreview` + `ResultText` (monospace). Color by `Risk` (ReadOnly=neutral, Write=amber, Execute=blue, Destructive=red) and by `Status`.
- Input bar: textbox (`InputText`), **Send** button (`SendCommand`), **Stop** button (`StopCommand`, visible/enabled when `IsBusy`), permission-mode `ComboBox` (`CurrentPermissionMode`), status text (`StatusText`), usage text (`LastUsageText`), workspace path label (`WorkspaceRoot`).
- **Inline approval card**: a panel bound to `PendingApproval` (visible when non-null via converter), showing tool name + risk + `ArgsPreview` + three buttons (Allow/Deny/AlwaysAllow → the `ApprovalRequestViewModel` commands).

### E.2 `ChatOnlyWindow.xaml` (MODIFY)
- Repoint DataContext to `AgentSessionViewModel`; minimal transcript + input + Stop. Keep borderless/floating/opacity behavior.

### E.3 `SettingsWindow.xaml` + `SettingsViewModel` (MODIFY/EXTEND)
Add controls + VM members:
- **Workspace directory picker**: textbox + “찾아보기” button (folder dialog via `System.Windows.Forms.FolderBrowserDialog` — WinForms already enabled). Binds `WorkspaceRoot`; persists via `UpdateWorkspaceRootAsync`.
- **Permission mode** selector (ComboBox of `PermissionMode`): Manual/Auto-Safe/Full-Auto, with Full-Auto risk warning text. Persists via `UpdatePermissionModeAsync`.
- **Max iterations** numeric (1–100). **Max tokens** numeric.
- **Server URL** textbox, **Auth scheme** combo (Bearer/ApiKey), **Auth token** password box, **Model id** combo (populated from `GetModelsAsync`, free-text fallback). Persist via `UpdateServerConfigAsync`.
- Keep existing hotkey + opacity settings UI.
- Remove any MCP port/enabled UI if present in current SettingsWindow.

### E.4 New converters (`Resources/Converters.xaml` + `Views/Converters.cs`)
- `ToolRiskToBrushConverter`, `ToolCallStatusToIcon/BrushConverter`, `NullToVisibilityConverter` (for `PendingApproval`), `BoolToVisibility` (likely exists — reuse).

---

## PART F — APP.XAML.CS WIRING (manual DI, no container)

Replace `OnStartup` service-construction block (current lines 36–61, 88–90) with this exact construction order. Field changes: remove `_mcpService`; change `_mainVm` type to `AgentSessionViewModel`; add fields for the new services that need lifetime/exit references (most are captured by the orchestrator/VM, so few new fields needed — only keep what `OnExit`/handlers touch).

Construction order (dependencies flow downward):
```
1.  _settingsService = new SettingsService();  await LoadAsync();          // first: everything reads it
2.  _httpClient = new HttpClient { BaseAddress = Uri(settings.ServerBaseUrl), Timeout = Infinite };
3.  var workspace   = new WorkspaceContext(_settingsService);              // C.3
4.  var scriptExec  = new ScriptExecutor();                                // reused
5.  var tools = new ITool[] {                                             // C.6, order = display order
        new RunCommandTool(scriptExec),
        new ReadFileTool(),  new WriteFileTool(), new EditFileTool(),
        new ListDirectoryTool(), new GlobTool(), new GrepTool(),
        new CreateDirectoryTool(),
        new MoveTool(), new CopyTool(), new DeleteTool() };
6.  var registry    = new ToolRegistry(tools);                             // C.2
7.  var permissions = new PermissionService(_settingsService);            // C.4
8.  var api         = new AgentApiClient(_httpClient, _settingsService);  // C.1
9.  var orchestrator= new AgentOrchestrator(api, registry, permissions, workspace, _settingsService); // C.5
10. _mainVm = new AgentSessionViewModel(orchestrator, api, permissions, workspace, _settingsService); // D.1
11. _mainWindow = new MainWindow(_mainVm); MainWindow = _mainWindow;
12. InitializeTrayIcon(); _trayNotification = new TrayNotificationService(_trayIcon!);
13. _windowCoordinator = new ChatWindowCoordinator(() => _mainWindow!, () => _mainVm!, _trayNotification);  // generic now AgentSessionViewModel
14. _globalHotkey = new GlobalHotkeyService(); + HotkeyPressed wiring (UNCHANGED, lines 74–83)
15. _settingsService.SettingsChanged += (_, s) => { workspace.SetRoot(s.WorkspaceRoot);  // keep workspace in sync
                                                    _globalHotkey!.Unregister(); _globalHotkey.Register(s.Hotkey); };
16. _mainWindow.Show();  _ = _mainVm.InitializeAsync();
```
Notes:
- Tools are stateless singletons; `RunCommandTool` is the only one needing a dependency (`scriptExec`).
- `OnExit`: drop MCP block; keep `_globalHotkey?.Dispose(); _trayIcon?.Dispose(); _httpClient?.Dispose();`. Make non-async.
- `SettingsWindow` creation in tray menu (lines 146–154) unchanged except `SettingsViewModel` ctor may now need `IAgentApiClient` for `GetModelsAsync` — **Decision: pass `api`** → store `api` in an `App` field `_api` and use it when building `SettingsViewModel`. So add field `private IAgentApiClient? _api;` and assign at step 8.

---

## PART G — FULL FILE MANIFEST

### DELETE (12)
```
Services/AgentActionService.cs
Services/IAgentActionService.cs
Services/McpSseServer.cs
Services/IMcpSseServer.cs
Services/McpRemoteAgentService.cs
Services/IRemoteAgentService.cs
Services/ChatService.cs
Services/IChatService.cs
Models/Mcp/McpRequest.cs
Models/Mcp/McpResponse.cs
Models/Mcp/McpError.cs
Models/Mcp/McpTool.cs
ViewModels/MainViewModel.cs
```
(13 entries — count includes MainViewModel.cs.)

### MODIFY
```
OhMyAgent.AiAgent.Client.csproj          (remove SemanticKernel)
App.xaml.cs                              (DI rewrite, remove MCP/Action refs, OnExit)
Models/AppSettings.cs                    (drop McpPort/McpEnabled; add Phase 1 fields; SchemaVersion=3)
Services/SettingsService.cs              (v2->v3 migration; new updaters)
Services/ISettingsService.cs             (new updater signatures)
Services/ScriptExecutor.cs               (add optional workingDirectory param)
Services/IScriptExecutor.cs              (add optional workingDirectory param)
MainWindow.xaml / MainWindow.xaml.cs     (transcript view; ctor type AgentSessionViewModel)
Views/ChatOnlyWindow.xaml / .xaml.cs     (DataContext type)
Views/SettingsWindow.xaml / .xaml.cs     (workspace/permission/server settings)
ViewModels/SettingsViewModel.cs          (new settings members + GetModelsAsync)
Services/ChatWindowCoordinator.cs / IChatWindowCoordinator.cs (generic VM type -> AgentSessionViewModel)
Resources/Converters.xaml                (new converters)
Views/Converters.cs                      (new converter classes)
```

### CREATE
```
Models/Agent/AgentEnums.cs               (MessageRole, ToolRisk, PermissionMode, PermissionDecision, StopReason)
Models/Agent/AgentMessage.cs
Models/Agent/ToolCall.cs
Models/Agent/ToolSchema.cs
Models/Agent/RequestMetadata.cs
Models/Agent/AgentRequest.cs
Models/Agent/Usage.cs
Models/Agent/ModelInfo.cs
Models/Agent/AgentStreamEvent.cs         (MessageStart/ContentDelta/ToolCallEvent/MessageStop/ErrorEvent)
Models/Agent/AgentEvent.cs               (orchestrator UI events)
Models/Agent/ToolResult.cs

Services/AgentJson.cs                    (shared JsonSerializerOptions)
Services/IAgentApiClient.cs
Services/AgentApiClient.cs
Services/ITool.cs
Services/IToolRegistry.cs
Services/ToolRegistry.cs
Services/ToolContext.cs
Services/IWorkspaceContext.cs
Services/WorkspaceContext.cs
Services/IPermissionService.cs
Services/PermissionService.cs
Services/IAgentOrchestrator.cs
Services/AgentOrchestrator.cs
Services/AgentSession.cs
Services/Tools/RunCommandTool.cs
Services/Tools/ReadFileTool.cs
Services/Tools/WriteFileTool.cs
Services/Tools/EditFileTool.cs
Services/Tools/ListDirectoryTool.cs
Services/Tools/GlobTool.cs
Services/Tools/GrepTool.cs
Services/Tools/CreateDirectoryTool.cs
Services/Tools/MoveTool.cs
Services/Tools/CopyTool.cs
Services/Tools/DeleteTool.cs
Services/Tools/ToolSchemas.cs            (JSON-schema parse helper + shared schema strings)

ViewModels/AgentSessionViewModel.cs
ViewModels/ApprovalRequestViewModel.cs
ViewModels/Transcript/ITranscriptItem.cs
ViewModels/Transcript/UserTurnViewModel.cs
ViewModels/Transcript/AssistantTurnViewModel.cs
ViewModels/Transcript/SystemNoticeViewModel.cs
ViewModels/Transcript/ToolCallViewModel.cs   (+ ToolCallStatus enum)

Views/TranscriptItemTemplateSelector.cs
Views/Controls/ToolCallCard.xaml (+ .cs)     (optional UserControl; may instead be a DataTemplate)
Views/Controls/ApprovalCard.xaml (+ .cs)     (optional; may be DataTemplate)
```

---

## 의존성 다이어그램

```
View (MainWindow / ChatOnlyWindow / SettingsWindow)
   │  binds
   ▼
AgentSessionViewModel ── PendingApproval ──> ApprovalRequestViewModel
   │  uses                                         ▲ (decision)
   ▼                                               │ SetApprovalHandler
IAgentOrchestrator ──> IAgentApiClient ──HTTP/SSE──> 사내 AI 서버
   │   │   │                                        /api/v1/agent/chat
   │   │   └─> IPermissionService ──> ISettingsService (PermissionMode)
   │   └─────> IToolRegistry ──> ITool[11]
   │                              └ RunCommandTool ──> ScriptExecutor + SecurityValidator (reused)
   │                              └ file tools ──────> IWorkspaceContext.ResolvePath (sandbox)
   └─────────> IWorkspaceContext ──> ISettingsService (WorkspaceRoot)

Models (pure data): AgentMessage/Request/ToolCall/ToolSchema/ToolResult/Usage/ModelInfo/
                    AgentStreamEvent*/AgentEvent*/enums   — depend on nothing
```

---

## 구현 제외 범위 (Phase 1에서 다루지 않음)
- 감사 로그(audit log) 파일 기록 — Phase 2.
- SecurityValidator 확장(전 도구 대상 위험 차단 규칙) — Phase 1은 `run_command`에만 기존 검증 적용.
- 토큰/시간 예산 제한 — Phase 1은 MaxIterations 캡만.
- 세션 영속화/복원, TODO·계획 패널 — Phase 3.
- 2차 도구(screenshot/ui_automation/process/registry/http_fetch/clipboard/env) — Phase 4.
- `stream:false` 폴백 경로 — 선택, Phase 1 필수 아님(서버는 SSE 우선).
- 인증 방식 확정(mTLS 등) — 서버팀 협의 대기; 클라이언트는 Bearer/ApiKey 둘 다 지원하도록만 구현.
- 영구(persistent) AlwaysAllow 규칙, 인자 단위 권한 — Phase 2(현재는 세션·도구명 단위).

## 가정 사항 (Assumptions)
1. Server endpoints are exactly `/api/v1/health`, `/api/v1/models`, `/api/v1/agent/chat` per API_CONTRACT.
2. `ParametersSchema`/`JsonSchema` realized as `System.Text.Json.JsonElement` (no external JSON Schema lib).
3. New agent wire DTOs use System.Text.Json; legacy settings persistence keeps Newtonsoft.
4. ReadOnly tools are auto-approved even in Manual mode (reads are safe); only Write/Execute/Destructive gate.
5. `MainViewModel`/`ChatService`/`IChatService` fully retired (clean replacement, no coexistence).
6. `ScriptExecutor` gains one optional `workingDirectory` param — the sole permitted change to reused executor.
