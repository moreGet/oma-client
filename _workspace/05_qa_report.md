# 05 — QA Report: 서버 API-SPEC ↔ 클라이언트 와이어 정합성 검증

검증자: QAReviewer. 방식: 코드 Read/grep + 실제 빌드 + 서버 `docs/API-SPEC.md` 교차 대조 + `git diff`(회귀 확인).

## 최종 판정: **PASS** (전체 통과, 직접 수정 0건)

빌드 신규 오류 0. 7개 정합성 항목 전부 서버 스펙과 일치. ITool 11개 무변경(회귀 없음). 발견된 결함 없음 → 직접 수정 불필요.

---

## 1. 빌드 — PASS

```
"/mnt/c/Program Files/dotnet/dotnet.exe" build OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client.csproj
→ 빌드했습니다.  경고 2개  오류 0개  (00:00:01)
```
- 경고는 `NU1510`(System.Drawing.Common pruning) 2건뿐 — 본 변경과 무관, 기존 패키지 이슈.
- 기존에 보고된 CS8767/CS8602 경고는 이번 빌드 출력에 나타나지 않음(범위 외, 무관).
- **신규 오류 0** 충족.

## 2. 인증 (치명1) — PASS
서버 스펙 교차 확인(`API-SPEC.md:14, 139`): `POST /api/v1/auth/login` Public, `{username,password}`→`{token,...}`, `Authorization: Bearer <token>`.

| 검증 항목 | 결과 | 근거 |
|----------|------|------|
| `IAgentApiClient.LoginAsync(username,password,ct)` 시그니처 | PASS | `IAgentApiClient.cs:20` |
| `AgentApiClient.LoginAsync` → `POST /api/v1/auth/login`, body `{username,password}` | PASS | `AgentApiClient.cs:20(LoginPath), 152-190`, `LoginRequestDto` JsonPropertyName username/password |
| 응답에서 `{token}` 파싱 | PASS | `LoginResponseDto`(`[JsonPropertyName("token")]`), `AgentApiClient.cs:174-180` |
| LoginAsync가 ApplyAuth **미부착**(Public) | PASS | `AgentApiClient.cs:159-163` — ApplyAuth 호출 없음 (주석 명시) |
| VM 토큰 단일 영속 경로 | PASS | `SettingsViewModel.LoginAsync` → `UpdateServerConfigAsync(...)` 단일 경로 (`SettingsViewModel.cs:150-152`) |
| `ApplyAuth` Bearer 고정 (ApiKey 분기 제거) | PASS | `AgentApiClient.cs:192-197` — `new AuthenticationHeaderValue("Bearer", token)` 고정, 분기 없음 |
| SettingsWindow.xaml AuthScheme 콤보 잔재 없음 | PASS | grep: XAML에 `AuthScheme`/`AuthSchemes` 바인딩 0건 |

> 비고: `AppSettings.AuthScheme`(="Bearer" 기본) 및 `SettingsService`의 Bearer 시드/`UpdateServerConfigAsync(scheme)` 시그니처는 설계서(§구현 제외)대로 **의도적 유지**. VM은 항상 리터럴 `"Bearer"`만 전달(`SettingsViewModel.cs:133,152`). 런타임 영향 없음.

## 3. content_delta (치명2) — PASS
서버 스펙(`API-SPEC.md:186`): `data: {"delta":"Sure, "}`.
- `AgentApiClient.cs:217-218`: `case "content_delta": return new ContentDelta(GetString(root, "delta"));`
- grep: `"text"` 잔재 0건. PASS.

## 4. tool_call arguments (치명3) — PASS
서버 스펙(`API-SPEC.md:166,189`): `arguments`는 JSON **문자열** (`"arguments":"{\"path\":\".\"}"`).

- **(a) 응답 파싱**: `AgentApiClient.cs:220-259` — `arguments`가 String이면 `JsonDocument.Parse`로 객체화, 이미 객체면 통과, 빈/깨짐이면 `{}`. `ToolCallEvent.Args`는 항상 JSON object. PASS.
- **(b) 요청 직렬화**: `ToolCallJsonConverter.Write`(`ToolCallJsonConverter.cs:45-53`)가 `value.Arguments.GetRawText()`를 `WriteString`으로 출력 → 와이어에 문자열 값. `AgentJson.cs:14-18` Options.Converters에 `new ToolCallJsonConverter()` 등록(어트리뷰트 미사용, 중복 없음). PASS.
- **ITool 회귀 — PASS**: `git status Services/Tools/` 0건 변경. 11개 도구 파일(CopyTool/CreateDirectoryTool/DeleteTool/EditFileTool/GlobTool/GrepTool/ListDirectoryTool/MoveTool/ReadFileTool/RunCommandTool/WriteFileTool) 무수정. 소비처(`AgentSessionViewModel.RenderArgs(JsonElement)`, `tool.ExecuteAsync(call.Arguments)`)는 인메모리 객체 표현을 그대로 받음. PASS.

## 5. usage (경미4) — PASS
서버 스펙(`API-SPEC.md:75,192`): `{prompt_tokens, completion_tokens, total_tokens}`.
- `Usage.cs:6-9`: `PromptTokens`/`CompletionTokens`/`TotalTokens=0` + 정확한 JsonPropertyName. PASS.
- 소비처 `AgentSessionViewModel.cs:661`: `$"in:{usage.PromptTokens} out:{usage.CompletionTokens}"`. grep: `InputTokens`/`OutputTokens` 잔재 0건. PASS.
- `message_stop` 생성(`AgentApiClient.cs:264-265`)의 `new Usage(0,0)`은 3-인자 record(TotalTokens 기본 0)라 컴파일 정상. PASS.

## 6. models (경미5) — PASS
서버 스펙(`API-SPEC.md:152`): `{id, name, provider_type, active}`.
- `ModelInfo.cs:6-10`: 정확히 일치. PASS.
- 소비처 `SettingsViewModel.LoadModelsAsync`는 `m.Id`만 사용(`SettingsViewModel.cs:121`). grep: 제거된 `DisplayName`/`SupportsTools`/`SupportsVision` 참조 0건. 깨짐 없음. PASS.

## 7. metadata (경미6) — PASS
서버 스펙(`API-SPEC.md:175`): `"metadata":{"os":"windows","workspace_root":"..."}`.
- `RequestMetadata.cs:8`: `[JsonPropertyName("workspace_root")]`. PASS.
- 호출부 `AgentOrchestrator.cs:171`: `new RequestMetadata("windows", workspace.Root, ClientVersion)` 위치 인자 → 무변경 컴파일. PASS.

## 8. 기타7 — PASS
- `AppSettings.cs:23`: `ModelId` 기본값 `""`. PASS.
- `SettingsService.cs:62`: `corp-llm-32b` 시드 블록 제거(주석 명시). grep: `corp-llm-32b` 잔재 0건. PASS.

## 9. MVVM / 누수 / async / null — PASS
- **바인딩 정합**: XAML 바인딩(Username/IsLoggedIn/LoginStatus/LoginCommand/AvailableModels/ModelId/ServerBaseUrl/MaxIterations/MaxTokens/SaveServerConfigCommand/LoadModelsCommand) 전부 VM 멤버에 매핑됨. 미해결 바인딩 0.
- **PasswordBox 패턴**: `LoginPasswordBox`/`AuthTokenBox` 모두 코드비하인드 `PasswordChanged` 푸시(`SettingsWindow.xaml.cs:46-57`). MVVM 위반 없음(PasswordBox 비바인딩 표준 패턴). `Password`는 비-ObservableProperty public 세터로 바인딩 회피.
- **async**: `LoginAsync`/`LoadModelsAsync`/`SaveServerConfigAsync`는 `[RelayCommand]`→`AsyncRelayCommand`(async Task). async void 남용 없음. PasswordChanged 핸들러는 동기. `LoginAsync`는 throw 대신 `LoginResult.Fail`로 변환(취소만 throw) → UI 크래시 위험 없음.
- **null 안전**: `result.Token!`은 `result.Success` 가드 후 사용(서비스가 성공 시 비-null 토큰 보장). `GetString` 폴백 `""`, `EmptyObject()` 방어. `dto?.Token` null 체크. 위험 역참조 없음.
- **DI**: `SettingsViewModel(ISettingsService, IAgentApiClient)` 생성자 주입 유지. `new Service()` 직접 생성 없음. App.xaml.cs 배선 무변경(메서드만 추가).

---

## 직접 수정한 항목
**없음.** 모든 검증 항목이 처음부터 PASS — 빌드 통과 상태 유지. 수정 불필요.

## 미해결 / 사용자 판단 필요 (블로커 아님, 본 작업 범위 밖)
| 위치 | 관찰 | 비고 |
|------|------|------|
| `AgentApiClient.cs:268-271` (`error` 이벤트) | 스트리밍 `error` 이벤트는 서버가 `{"error":{"code","message"}}`로 **중첩**(`API-SPEC.md:196`) 전달하나, `Dispatch`는 `GetString(root,"code")`/`GetString(root,"message")`로 **평면** 파싱 → 스트림 중 오류 시 code/message가 빈 문자열로 떨어짐. | 기존 코드(이번 7개 수정과 무관), 설계서 §구현 제외(에러 envelope 통합 범위 밖). 사용자 판단. 수정 시 `root.TryGetProperty("error", out var e)` 후 e에서 추출 권장. |
| `AppSettings.AuthScheme` / `SettingsService` Bearer 시드 / `UpdateServerConfigAsync(scheme)` | 의도적 잔존(설계서 §제외 — scheme 파라미터 제거 리팩토링 범위 밖). | 무해. 추후 정리 가능. |

## 회귀 확인 요약 (git)
- `Services/Tools/` — 0 변경 (ITool 11개 무수정). ✓
- 변경 파일은 설계서 분담 범위와 정확히 일치(Models 5 + Services 5 + ViewModels 2 + Views 2 + 신규 LoginResult/ToolCallJsonConverter). 범위 외 코드 변경 없음.
