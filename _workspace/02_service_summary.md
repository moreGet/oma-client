# 02 — ServiceEngineer Summary (Models + Services 레이어)

설계서 `01_architect_spec.md` 의 ServiceEngineer 분담을 전부 구현 완료. 서버 미변경, 클라이언트만 수정.
`Services/Tools/*` 11개 ITool 구현은 무수정(인메모리 JsonElement 객체 표현 불변). ViewModels/Views 미변경.

## 변경 파일 목록 + 요약

### Models
| 파일 | 변경 | 요약 |
|------|------|------|
| `Models/Agent/Usage.cs` | 수정 | 필드명 `input_tokens/output_tokens` → `prompt_tokens/completion_tokens` (+`total_tokens` 추가, 기본값 0). C# 프로퍼티: `InputTokens/OutputTokens` → `PromptTokens/CompletionTokens/TotalTokens`. |
| `Models/Agent/ModelInfo.cs` | 수정 | `{id, display_name, supports_tools, supports_vision}` → `{id, name, provider_type, active}`. C# 프로퍼티: `Id/Name/ProviderType/Active`. |
| `Models/Agent/RequestMetadata.cs` | 수정 | JsonPropertyName `workspace` → `workspace_root`. C# 인자명 `Workspace` → `WorkspaceRoot` (호출부는 위치 인자라 무영향). |
| `Models/AppSettings.cs` | 수정 | `ModelId` 기본값 `"corp-llm-32b"` → `""`. `AuthScheme` 프로퍼티는 **유지**(설계서 지침: scheme 파라미터 제거 리팩토링은 범위 밖). |
| `Models/Agent/LoginResult.cs` | **신규** | 로그인 결과 DTO. `record LoginResult(bool Success, string? Token, string? ErrorMessage)` + 팩토리 `Ok(token)`/`Fail(message)`. |

### Services
| 파일 | 변경 | 요약 |
|------|------|------|
| `Services/ToolCallJsonConverter.cs` | **신규** | `JsonConverter<ToolCall>`. Write: `arguments`(JsonElement 객체)를 `GetRawText()`로 JSON 텍스트 추출 후 `WriteString`으로 **문자열 값** 출력(와이어: `"arguments":"{\"...\"}"`). Read: 문자열이면 재파싱하여 객체 복원(대칭). 프로퍼티명 `id/name/arguments`를 컨버터 내부에서 명시. |
| `Services/AgentJson.cs` | 수정 | `Options.Converters`에 `new ToolCallJsonConverter()` 추가(어트리뷰트 미사용 — Options 단일 등록). |
| `Services/IAgentApiClient.cs` | 수정 | `LoginAsync` 시그니처 추가(아래). 기존 3개 메서드 불변. |
| `Services/AgentApiClient.cs` | 수정 | `LoginPath` 상수 추가; `LoginAsync` 구현(POST `/api/v1/auth/login`, ApplyAuth 미부착, throw 대신 `LoginResult` 변환); `ApplyAuth`에서 `ApiKey`/`X-Api-Key` 분기 제거→Bearer 고정; content_delta 파싱 `"text"`→`"delta"`; tool_call `arguments`가 JSON string이면 `JsonDocument.Parse`로 객체화(이미 객체면 통과, 깨지면 `{}`); private `LoginRequestDto`/`LoginResponseDto` record 추가. |
| `Services/SettingsService.cs` | 수정 | v3 마이그레이션의 `ModelId = "corp-llm-32b"` 시드 블록 제거(빈 문자열 유지). `AuthScheme="Bearer"` 시드 및 `UpdateServerConfigAsync` 시그니처는 유지. |

`Services/AgentOrchestrator.cs`: 확인만 — `BuildRequest`의 `new RequestMetadata("windows", workspace.Root, ClientVersion)`(위치 인자)와 `new Usage(0,0)` 모두 무변경으로 컴파일.

## ViewModelEngineer / UIDesigner 가 알아야 할 인터페이스

### 신규: `IAgentApiClient.LoginAsync`
```csharp
Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);
```
- **POST /api/v1/auth/login (Public)** — 토큰을 **저장하지 않고 반환만** 한다(영속은 VM 책임).
- 반환 `LoginResult`(record, namespace `OhMyAgent.AiAgent.Client.Models`):
  - 성공: `Success == true`, `Token`(비어있지 않음), `ErrorMessage == null`.
  - 실패: `Success == false`, `Token == null`, `ErrorMessage`(표시용 메시지). 네트워크 예외도 throw하지 않고 `Fail`로 반환(취소 예외만 throw).
- VM 흐름(설계서대로): 성공 시 `AuthToken = result.Token!` → `_settings.UpdateServerConfigAsync(ServerBaseUrl, "Bearer", AuthToken, ModelId, MaxIterations, MaxTokens)`로 영속.

### 제거된 것 (VM/View 동반 수정 필요)
- `AgentApiClient.ApplyAuth`의 `ApiKey`/`X-Api-Key` 분기 삭제, **Bearer 고정**.
  → `SettingsViewModel`의 `AuthScheme`/`AuthSchemes` 제거하고, `UpdateServerConfigAsync` 호출 시 `scheme` 인자에 리터럴 `"Bearer"` 전달.
  → `AppSettings.AuthScheme` 프로퍼티 및 `UpdateServerConfigAsync(.., scheme, ..)` 시그니처 자체는 **유지**(파급 최소화). VM은 항상 `"Bearer"`만 넘기면 됨.

### 변경된 프로퍼티명 (소비처 동반 수정 필요)
- **Usage**: `InputTokens` → `PromptTokens`, `OutputTokens` → `CompletionTokens` (+신규 `TotalTokens`).
  → `AgentSessionViewModel.cs:661` (`LastUsageText = $"in:{usage.InputTokens} out:{usage.OutputTokens}"`)를 `usage.PromptTokens`/`usage.CompletionTokens`로 교체해야 컴파일됨. **ViewModelEngineer 담당.**
- **ModelInfo**: `DisplayName/SupportsTools/SupportsVision` → `Name/ProviderType/Active`.
  → 소비처 `SettingsViewModel.LoadModelsAsync`는 `m.Id`만 사용 → 무영향. MainWindow.xaml `DisplayName` 바인딩은 `WorkspaceHistoryEntry`용이라 무관.

### 기본값 변경
- `AppSettings.ModelId` 기본값 `""`(빈 문자열). 신규 사용자는 `/models`에서 선택. UI에서 빈 ModelId 상태 처리(선택 유도) 고려.

## 추가 사항/판단
- 설계서의 권장대로 `ToolCallJsonConverter`는 어트리뷰트가 아닌 `AgentJson.Options` 단일 등록(중복 등록 없음).
- `LoginResponseDto`는 `AgentJson.Options`(Web defaults, camelCase)로 역직렬화하되 `[JsonPropertyName("token")]`로 명시 매핑 — 서버 `{token}` 안전 파싱.
- 빌드 최종 검증은 오케스트레이터 담당.
