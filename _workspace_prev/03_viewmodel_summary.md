# 03 — ViewModel 레이어 구현 요약 (ViewModelEngineer)

> Namespace: `OhMyAgent.AiAgent.Client.ViewModels` (transcript items: `...ViewModels.Transcript`).
> CommunityToolkit.Mvvm source generators. Nullable enabled, file-scoped namespaces, sealed where appropriate.
> All VM mutations from the orchestrator stream are marshalled onto `Application.Current.Dispatcher` by the VM.

## Phase 0
- **DELETED** `ViewModels/MainViewModel.cs` (replaced by `AgentSessionViewModel`).
- `ViewModels/ChatMessageViewModel.cs` KEPT untouched (spec A.2 lists it under KEEP; not used by the new transcript).

---

## AgentSessionViewModel  (root VM — bound to MainWindow & ChatOnlyWindow)
File: `ViewModels/AgentSessionViewModel.cs` — `sealed partial class : ObservableObject`

### EXACT constructor signature (ServiceEngineer wires this in App.xaml.cs step 10)
```csharp
public AgentSessionViewModel(
    IAgentOrchestrator orchestrator,
    IAgentApiClient    api,
    IPermissionService permissions,
    IWorkspaceContext  workspace,
    ISettingsService   settings);
```
- At ctor: `permissions.SetApprovalHandler(RequestApprovalAsync)` registered.
- `Func<...>` shape consumed: `Func<ToolCall, ToolRisk, CancellationToken, Task<PermissionDecision>>` (matches C.4).
- After construction, App must call `_ = vm.InitializeAsync();` (seeds settings, health-checks, adds greeting).

### Bindable observable properties (for UIDesigner)
| Property | Type | Notes |
|---|---|---|
| `InputText` | string | two-way; drives `SendCommand.CanExecute` |
| `IsBusy` | bool | loop running; drives Send/Stop CanExecute |
| `IsConnected` | bool | server health |
| `HasError` | bool | |
| `ErrorMessage` | string | |
| `StatusText` | string | "연결 중..." / "Connected" / "실행 중 (i/max)" / "완료" / "중지됨" / "오류" |
| `WorkspaceRoot` | string | header display (read from settings) |
| `CurrentPermissionMode` | PermissionMode | two-way selector; setter persists via `UpdatePermissionModeAsync` |
| `WindowOpacity` | double | two-way; setter persists via `UpdateOpacityAsync` (clamped 0.3–1.0) |
| `PendingApproval` | `ApprovalRequestViewModel?` | non-null ⇒ show inline approval card (bind Visibility via NullToVisibility) |
| `LastUsageText` | string | "in:1200 out:80" |

### Collections
- `Transcript` : `ObservableCollection<ITranscriptItem>` (root items source).
- `PermissionModes` : `IReadOnlyList<PermissionMode>` = [Manual, AutoSafe, FullAuto] (for the header ComboBox).

### Commands
| Command | Kind | CanExecute | Behavior |
|---|---|---|---|
| `SendCommand` | AsyncRelayCommand | `!IsBusy && !blank(InputText)` | snapshot InputText → add `UserTurnViewModel`; new CTS; `IsBusy=true`; `await foreach orchestrator.RunAsync(goal, session, ct)` → project each AgentEvent onto Transcript (UI thread); `finally IsBusy=false`. |
| `StopCommand` | RelayCommand | `IsBusy` | `_cts?.Cancel()`. |
| `RetryConnectionCommand` | AsyncRelayCommand | always | `IsConnected = await api.CheckHealthAsync()`; sets StatusText/error. |
| `ClearCommand` | RelayCommand | always | `Transcript.Clear()`; new `AgentSession`; `permissions.ClearSessionRules()`. |

> Note: no `PickWorkspaceCommand` on this VM — workspace selection is handled in `SettingsViewModel` (Part E.3 folder dialog) per spec ("OR handled in Settings"). Documented assumption below.

### AgentEvent → Transcript projection (D.4, all on UI thread)
- `AgentTextDelta` → ensure trailing streaming `AssistantTurnViewModel`, append `.Text`.
- `AgentAssistantMessageComplete` → current assistant `IsStreaming=false`, close it.
- `AgentToolCallStarted` → add `ToolCallViewModel{Status=Running}`, indexed by `CallId`.
- `AgentAwaitingApproval` → set that card `Status=AwaitingApproval`.
- `AgentToolCallResult` → set `ResultText`, `IsError`, `Status` (Succeeded / Failed / Denied when content=="Denied by user").
- `AgentIterationAdvanced` → `StatusText="실행 중 (i/max)"`.
- `AgentDone` → close assistant, `StatusText="완료"`, set `LastUsageText`.
- `AgentError` → `HasError=true`, `ErrorMessage`, append `SystemNoticeViewModel`.

---

## Transcript item VMs — `ViewModels/Transcript/`
`ITranscriptItem` is a marker interface — **UIDesigner's `TranscriptItemTemplateSelector` keys off the concrete type**.

| Type | Bindable members |
|---|---|
| `UserTurnViewModel` | `Text` (string, init) |
| `AssistantTurnViewModel` | `Text` (string, observable, streamed), `IsStreaming` (bool, observable) |
| `SystemNoticeViewModel` | `Text` (string, init) |
| `ToolCallViewModel` | `CallId` `ToolName` (string, init), `Risk` (ToolRisk, init), `ArgsPreview` (string, init, pretty JSON), `Status` (ToolCallStatus, observable), `ResultText` (string, observable), `IsError` (bool, observable), `IsExpanded` (bool, observable — collapsible card) |

`enum ToolCallStatus { Running, AwaitingApproval, Succeeded, Failed, Denied }` (in `ToolCallViewModel.cs`).

---

## ApprovalRequestViewModel — `ViewModels/ApprovalRequestViewModel.cs`
`sealed partial class : ObservableObject` — backs the inline approval card (`PendingApproval`).
- Read-only: `ToolName` (string), `Risk` (ToolRisk), `ArgsPreview` (string, pretty JSON).
- Commands: `AllowCommand`, `DenyCommand`, `AlwaysAllowCommand` (RelayCommand) → resolve `Task<PermissionDecision>`.
- `Task<PermissionDecision> WaitForDecisionAsync(CancellationToken ct)` — TaskCompletionSource; cancellation resolves to `Deny`.

---

## SettingsViewModel  (EXTENDED) — `ViewModels/SettingsViewModel.cs`
### Constructor signature CHANGED (ServiceEngineer: App.xaml.cs tray-menu SettingsWindow creation must pass `_api`)
```csharp
public SettingsViewModel(ISettingsService settings, IAgentApiClient api);
```
Call `await vm.InitializeAsync()` after creation to populate `AvailableModels`.

### New bindable members for UIDesigner (Part E.3)
| Member | Type | Persists via |
|---|---|---|
| `WorkspaceRoot` | string | `SetWorkspaceRootAsync(path)` (call from folder-dialog code-behind) → `UpdateWorkspaceRootAsync` |
| `PermissionMode` | PermissionMode (two-way) | setter → `UpdatePermissionModeAsync` |
| `PermissionModes` | `IReadOnlyList<PermissionMode>` | combo items |
| `ShowFullAutoWarning` | bool (computed) | show Full-Auto risk warning |
| `MaxIterations` | int | `SaveServerConfigCommand` |
| `MaxTokens` | int | `SaveServerConfigCommand` |
| `ServerBaseUrl` | string | `SaveServerConfigCommand` |
| `AuthScheme` | string | `SaveServerConfigCommand` |
| `AuthSchemes` | `IReadOnlyList<string>` = [Bearer, ApiKey] | combo items |
| `AuthToken` | string (password box) | `SaveServerConfigCommand` |
| `ModelId` | string (combo + free text) | `SaveServerConfigCommand` |
| `AvailableModels` | `ObservableCollection<string>` | model combo source |

New commands: `SaveServerConfigCommand` (AsyncRelayCommand → `UpdateServerConfigAsync(ServerBaseUrl, AuthScheme, AuthToken, ModelId, MaxIterations, MaxTokens)`), `LoadModelsCommand` (AsyncRelayCommand → `GetModelsAsync`).
- Existing hotkey-capture members and `SaveCommand`/`StartCaptureCommand`/`CancelCaptureCommand`/`ApplyCapturedKey` are PRESERVED unchanged.

---

## Dependencies on ServiceEngineer (interface contract — must match exactly)
- `IAgentOrchestrator.RunAsync(string, AgentSession, CancellationToken)` → `IAsyncEnumerable<AgentEvent>`
- `IAgentApiClient.CheckHealthAsync(ct)` , `GetModelsAsync(ct)` → `Task<IReadOnlyList<ModelInfo>>` (uses `ModelInfo.Id`)
- `IPermissionService.SetApprovalHandler(Func<ToolCall, ToolRisk, CancellationToken, Task<PermissionDecision>>)`, `ClearSessionRules()`
- `IWorkspaceContext.Root` (string)
- `AgentSession` — parameterless `new()`
- `ISettingsService` new: `UpdatePermissionModeAsync(PermissionMode)`, `UpdateWorkspaceRootAsync(string)`, `UpdateServerConfigAsync(string,string,string,string,int,int)`; existing `UpdateOpacityAsync`, `UpdateHotkeyAsync`; `Current.{WorkspaceRoot,PermissionMode,MaxIterations,MaxTokens,ServerBaseUrl,AuthScheme,AuthToken,ModelId,Opacity,Hotkey}`.

## Assumptions / notes
1. **Workspace picker is in Settings, not on AgentSessionViewModel.** Spec D.1 listed `PickWorkspaceCommand` as "OR handled in Settings"; I implemented it in `SettingsViewModel.SetWorkspaceRootAsync` (Part E.3). `AgentSessionViewModel.WorkspaceRoot` is display-only and re-seeded by `InitializeAsync` / settings.
2. `SendAsync` takes no CancellationToken parameter; it owns its own `CancellationTokenSource` so `StopCommand` can cancel it (AsyncRelayCommand's built-in token would only cancel on command-level dispose).
3. ReadOnly auto-approval, AlwaysAllow session scope etc. live entirely in `PermissionService`; the VM only renders the approval card when the handler is invoked.
4. UI marshalling: VM uses `Application.Current.Dispatcher` (CheckAccess fast-path) — this is the only place the VM touches a WPF type, justified by spec D.1 ("VM marshals").
5. `ChatWindowCoordinator` only uses the VM as a DataContext (no member access) — ServiceEngineer just changes its generic `Func<MainViewModel>` → `Func<AgentSessionViewModel>`.
