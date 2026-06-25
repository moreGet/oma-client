# 01 — Architect Spec: 서버 API-SPEC ↔ 클라이언트 와이어 포맷 정합성 수정

## 기능 명세

서버(`OhMyAgent.AiAgent.Server`)는 **불변**. 클라이언트(`OhMyAgent.AiAgent.Client`)만 수정하여 7개 불일치(치명 3 / 경미 3 / 기타 1)를 서버 `docs/API-SPEC.md` 계약에 맞춘다:
JWT 로그인 추가, SSE `delta` 필드명, `tool_call.arguments`의 JSON-string 양방향 처리, `usage`/`models`/`metadata` 필드명 정렬, 기본 ModelId 정리.

> 검증 출처: `/mnt/c/Users/dkdlw/GolandProjects/OhMyAgent.AiAgent.Server/docs/API-SPEC.md` (§인증, §C# 에이전트 클라이언트 계약).

---

## 핵심 설계 결정 (요약)

| # | 항목 | 결정 |
|---|------|------|
| 1 | 로그인 | `IAgentApiClient.LoginAsync(username, password, ct)` 추가 → `LoginResult` DTO 반환. 성공 시 토큰을 `SettingsService.AuthToken`에 영속. 설정창에 ID/PW + 로그인 버튼 + 상태. |
| 1 | AuthScheme | `ApiKey` 분기 **제거**, Bearer 고정. `AuthSchemes` 콤보·`X-Api-Key` 경로 삭제. |
| 2 | content_delta | 파싱을 `"text"` → `"delta"` 로 교정. `ContentDelta(Text)` 레코드는 유지. |
| 3a | tool_call 응답 | arguments가 JSON **string**이면 `JsonDocument.Parse`로 객체화하여 `ToolCallEvent.Args`(JsonElement 객체)로 담음. 이미 객체면 그대로. |
| 3b | tool_call 요청 | `ToolCall.Arguments`(JsonElement 객체)를 **JSON 문자열**로 직렬화하는 커스텀 `JsonConverter<ToolCall>` 추가. ITool 구현은 불변. |
| 4 | usage | `Usage` 필드명 `prompt_tokens`/`completion_tokens`(+`total_tokens`)로 정렬. C# 프로퍼티명도 변경 → 소비처(2곳) 동반 수정. |
| 5 | models | `ModelInfo`를 `{id,name,provider_type,active}`로 정렬. `AvailableModels`는 `.Id`만 쓰므로 바인딩 영향 없음. |
| 6 | metadata | `RequestMetadata`의 `"workspace"` → `"workspace_root"`. `client_version`은 **유지**(서버 무시, 무해). |
| 7 | 기본 ModelId | `"corp-llm-32b"` → `""`(빈 문자열). 마이그레이션 시드값도 빈 문자열로. `/models` 선택 유도. |

---

## Models

| 파일 | 변경 | 내용 |
|------|------|------|
| `Models/Agent/Usage.cs` | 수정 | 필드명·JsonPropertyName 변경. `total_tokens` 추가(선택). |
| `Models/Agent/ModelInfo.cs` | 수정 | 서버 스펙 필드로 전면 정렬. |
| `Models/Agent/RequestMetadata.cs` | 수정 | `JsonPropertyName("workspace")` → `"workspace_root"`. |
| `Models/Agent/ToolCall.cs` | 수정 | `[JsonConverter(typeof(ToolCallJsonConverter))]` 부착(또는 `AgentJson.Options`에 컨버터 등록 — 후자 권장, 아래 참조). 레코드 형태(Id/Name/Arguments) 자체는 불변. |
| `Models/Agent/AppSettings.cs` (실제 경로 `Models/AppSettings.cs`) | 수정 | `ModelId` 기본값 `"corp-llm-32b"` → `""`. |
| `Models/Agent/LoginResult.cs` | **신규** | 로그인 결과 DTO (아래). |

### Usage.cs (변경 후 정확한 시그니처)

```csharp
public sealed record Usage(
    [property: JsonPropertyName("prompt_tokens")]     int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")]      int TotalTokens = 0);
```
- 기존 `Usage(0, 0)` 생성자 호출 2곳(`AgentApiClient.Dispatch`)은 `Usage(0, 0, 0)` 또는 `new Usage(0,0)`(TotalTokens 기본값) 으로 컴파일 유지 가능. **3-인자 record라 `new Usage(0,0)`도 유효**(TotalTokens 기본 0).

### ModelInfo.cs (변경 후)

```csharp
public sealed record ModelInfo(
    [property: JsonPropertyName("id")]            string Id,
    [property: JsonPropertyName("name")]          string Name,
    [property: JsonPropertyName("provider_type")] string ProviderType,
    [property: JsonPropertyName("active")]        bool Active);
```
- 소비처 `SettingsViewModel.LoadModelsAsync`는 `m.Id`만 사용 → **무변경**.
- `MainWindow.xaml:82`의 `DisplayName` 바인딩은 `WorkspaceHistoryEntry`(프로젝트 칩)용이며 ModelInfo와 무관 → **영향 없음**.

### RequestMetadata.cs (변경 후)

```csharp
public sealed record RequestMetadata(
    [property: JsonPropertyName("os")]             string Os,
    [property: JsonPropertyName("workspace_root")] string WorkspaceRoot,   // ← 필드명만 변경 (workspace → workspace_root)
    [property: JsonPropertyName("client_version")] string ClientVersion);
```
- C# 생성자 인자명 `Workspace` → `WorkspaceRoot`로 바꿔도 무방하나, `AgentOrchestrator.BuildRequest`는 **위치 인자**(`new RequestMetadata("windows", workspace.Root, ClientVersion)`)로 호출 → **호출부 무변경**. JsonPropertyName만 바뀌면 와이어 정합 충족.

### LoginResult.cs (신규)

```csharp
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>POST /api/v1/auth/login 결과. 성공 시 Token 보유.</summary>
public sealed record LoginResult(bool Success, string? Token, string? ErrorMessage)
{
    public static LoginResult Ok(string token)       => new(true, token, null);
    public static LoginResult Fail(string message)   => new(false, null, message);
}
```
> 로그인 응답 본문 파싱용 내부 DTO는 `AgentApiClient` 내부 private record로 둔다(서버: `{token, ...}`):
> ```csharp
> private sealed record LoginResponseDto(
>     [property: JsonPropertyName("token")] string? Token);
> ```

---

## Service 인터페이스

### IAgentApiClient.cs — 메서드 1개 추가

```csharp
/// <summary>POST /api/v1/auth/login (Public). {username,password} → {token}. 성공 시 Token 보유한 LoginResult.</summary>
Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default);
```
- 기존 3개 메서드(`SendAsync`/`CheckHealthAsync`/`GetModelsAsync`) 시그니처는 불변.

### AgentApiClient.cs — 구현 변경 (정확한 위치)

1. **`LoginPath` 상수 추가**: `private const string LoginPath = "/api/v1/auth/login";`
2. **`LoginAsync` 구현 (신규)**:
   - `POST {LoginPath}` body `{"username":..,"password":..}` (System.Text.Json, `AgentJson.Options` 또는 익명객체).
   - **`ApplyAuth` 호출 금지**(Public 엔드포인트, 토큰 발급 전).
   - 200 → 본문에서 `token` 추출, 비어있지 않으면 `LoginResult.Ok(token)`. 토큰 없으면 `Fail`.
   - 비200 → `ReadErrorAsync` 재사용해 code/message 추출 후 `LoginResult.Fail(message)`.
   - 네트워크 예외 → `LoginResult.Fail(연결 실패 메시지)` (throw하지 않고 결과로 변환 — VM이 상태표시).
   - **주의**: 토큰을 `SettingsService`에 저장하는 것은 **VM 책임**(아래). 클라이언트는 결과만 반환.
3. **`ApplyAuth` 단순화** (line 151-162): `AuthScheme`/`ApiKey` 분기 **삭제**, Bearer 고정.
   ```csharp
   private void ApplyAuth(HttpRequestMessage req)
   {
       var token = settings.Current.AuthToken;
       if (!string.IsNullOrWhiteSpace(token))
           req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
   }
   ```
4. **`Dispatch` content_delta** (line 182-183): `GetString(root, "text")` → `GetString(root, "delta")`.
5. **`Dispatch` tool_call arguments** (line 185-192): JSON-string 방어 처리.
   ```csharp
   case "tool_call":
       JsonElement args;
       if (root.TryGetProperty("arguments", out var argsEl))
       {
           if (argsEl.ValueKind == JsonValueKind.String)
           {
               // 서버는 arguments를 JSON 문자열로 전달 → 한 번 더 파싱해 객체화.
               var raw = argsEl.GetString();
               if (string.IsNullOrWhiteSpace(raw)) { args = EmptyObject(); }
               else
               {
                   try { using var inner = JsonDocument.Parse(raw); args = inner.RootElement.Clone(); }
                   catch (JsonException) { args = EmptyObject(); }
               }
           }
           else
           {
               args = argsEl.Clone();   // 이미 객체/배열인 경우 방어적 통과
           }
       }
       else { args = EmptyObject(); }
       return new ToolCallEvent(GetString(root, "id"), GetString(root, "name"), args);
   ```
   > `ToolCallEvent.Args`는 항상 **JSON object**(JsonElement)로 유지 → 하류 `RenderArgs`/`ToolCall` 생성/ITool 파싱 전부 불변.
6. **`message_stop` usage** (line 196-199): `Usage(0,0)` → `Usage(0,0,0)` 또는 그대로(3번째 인자 기본값). 컴파일 확인만.

### AgentJson.cs — ToolCall 컨버터 등록 (요청 직렬화 3b)

`AgentJson.Options.Converters`에 `ToolCallJsonConverter` 추가:
```csharp
Converters =
{
    new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
    new ToolCallJsonConverter()   // ← 추가
},
```
> **권장 방식**: `[JsonConverter]` 어트리뷰트를 `ToolCall`에 붙이는 대신 **Options에 등록**한다. 이유: `ToolCall`은 `AgentMessage.ToolCalls` 리스트로도, `ToolCallEvent` 매핑으로도 쓰이는데, 직렬화 경로(요청 전송)는 전부 `AgentJson.Options`를 통과(`AgentApiClient.SendAsync` line 25)하므로 Options 등록 한 곳으로 전 경로 커버. 어트리뷰트 방식도 가능하나 둘 중 하나만 사용.

### ToolCallJsonConverter.cs (신규, `Services/` 또는 `Models/Agent/`)

`arguments`를 **쓰기 시 JSON 문자열로**, **읽기 시 문자열→객체**로 변환. (읽기 경로는 현재 미사용이나 대칭성/안전을 위해 구현.)

```csharp
public sealed class ToolCallJsonConverter : JsonConverter<ToolCall>
{
    public override ToolCall Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var id   = root.TryGetProperty("id", out var i)   ? i.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        JsonElement args;
        if (root.TryGetProperty("arguments", out var a))
        {
            if (a.ValueKind == JsonValueKind.String)
            {
                var raw = a.GetString();
                using var inner = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                args = inner.RootElement.Clone();
            }
            else args = a.Clone();
        }
        else { using var e = JsonDocument.Parse("{}"); args = e.RootElement.Clone(); }

        return new ToolCall(id, name, args);
    }

    public override void Write(Utf8JsonWriter writer, ToolCall value, JsonSerializerOptions o)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        // arguments(JsonElement 객체) → JSON 문자열로 직렬화해서 string 값으로 기록.
        writer.WriteString("arguments", value.Arguments.GetRawText());
        writer.WriteEndObject();
    }
}
```
> 핵심: `value.Arguments.GetRawText()`가 `{"path":"."}` 같은 **JSON 텍스트**를 반환하고, `WriteString`이 이를 **문자열 값**으로 이스케이프하여 와이어에는 `"arguments":"{\"path\":\".\"}"`가 나간다(서버 기대 형식).
> `[JsonPropertyName]`을 ToolCall 레코드에 두더라도 커스텀 Write가 직접 프로퍼티명을 쓰므로 무시됨 — 프로퍼티명을 컨버터 내부에서 명시(`id`/`name`/`arguments`).

### SettingsService.cs — 마이그레이션 기본값 (기타 7)

- line 62-63: `if (string.IsNullOrEmpty(Current.ModelId)) Current.ModelId = "corp-llm-32b";` → 이 블록 **삭제**(빈 문자열 유지). 또는 기본값을 `""`로.
- `UpdateServerConfigAsync`는 그대로 사용(시그니처 불변). **단** `scheme` 파라미터는 이제 항상 `"Bearer"`. 시그니처 유지하되 호출부에서 항상 Bearer 전달(아래 VM 참조). *선택*: scheme 파라미터를 제거하는 리팩토링은 범위 밖 — 유지 권장(파급 최소화).
- (선택) v4→v5 마이그레이션 블록 추가 검토: 기존 사용자의 `ModelId == "corp-llm-32b"`를 빈 문자열로 청소하고 싶다면 `SchemaVersion < 5` 블록에서 처리. **판단: 불필요**(기존 저장값이 실제 서버 모델과 다르면 사용자가 /models로 재선택하면 됨). 신규 기본값만 `""`로.

---

## ViewModels

### SettingsViewModel.cs — 로그인 + AuthScheme 제거

**추가 프로퍼티/커맨드:**
| 멤버 | 종류 | 용도 |
|------|------|------|
| `Username` | `[ObservableProperty] string` | 로그인 ID 입력 |
| `Password` | (PasswordBox 비바인딩 → public 세터 필드, AuthToken 패턴과 동일) | 로그인 PW |
| `LoginStatus` | `[ObservableProperty] string` | "로그인됨" / "실패: ..." / "로그인 중..." 상태 표시 |
| `IsLoggedIn` | `[ObservableProperty] bool` | 토큰 보유 여부(UI 게이팅용, 선택) |
| `LoginCommand` | `[RelayCommand]` `LoginAsync` | 로그인 실행 |

**`LoginAsync` 흐름:**
```
LoginStatus = "로그인 중...";
var result = await _api.LoginAsync(Username, Password);
if (result.Success) {
    AuthToken = result.Token!;                  // 기존 AuthToken 프로퍼티 재사용
    await _settings.UpdateServerConfigAsync(
        ServerBaseUrl, "Bearer", AuthToken, ModelId, MaxIterations, MaxTokens); // 토큰 영속
    IsLoggedIn = true;
    LoginStatus = "로그인됨";
} else {
    IsLoggedIn = false;
    LoginStatus = $"실패: {result.ErrorMessage}";
}
```

**제거:**
- `AuthScheme` `[ObservableProperty]` (line 43) **삭제**.
- `AuthSchemes` 리스트 (line 47) **삭제**.
- 생성자 `AuthScheme = c.AuthScheme;` (line 68) **삭제**.
- `SaveServerConfigAsync` (line 117-118): `AuthScheme` 인자 → 리터럴 `"Bearer"`로 교체:
  `await _settings.UpdateServerConfigAsync(ServerBaseUrl, "Bearer", AuthToken, ModelId, MaxIterations, MaxTokens);`

**유지:** `AuthToken` 프로퍼티는 로그인 결과 저장소로 계속 사용(수동 입력 PasswordBox는 제거하거나 읽기전용 표시로 전환 — UIDesigner 판단).

### AgentSessionViewModel.cs — Usage 프로퍼티명 (경미 4)

- line 660-661:
  ```csharp
  if (done.LastUsage is { } usage)
      LastUsageText = $"in:{usage.InputTokens} out:{usage.OutputTokens}";
  ```
  → `usage.PromptTokens` / `usage.CompletionTokens` 로 교체:
  ```csharp
  LastUsageText = $"in:{usage.PromptTokens} out:{usage.CompletionTokens}";
  ```
  (표시 라벨 in/out는 유지. 원하면 `total:{usage.TotalTokens}` 추가 가능 — 선택.)
- `AgentSession.LastUsage`(타입 `Usage?`)는 타입만 참조 → **무변경**. `AgentDone(string, Usage?)`도 무변경.

---

## Views

### SettingsWindow.xaml — 로그인 UI + AuthScheme 콤보 제거 (서버 설정 카드, line 205-287)

**제거:**
- `AuthScheme` ComboBox (line 232-240) **삭제**(`AuthSchemes`/`AuthScheme` 바인딩 제거됨).

**추가 (서버 설정 카드 내, 인증 토큰 위 또는 대체):**
- "사용자 ID" TextBox → `{Binding Username}`
- "비밀번호" PasswordBox `x:Name="LoginPasswordBox"` + `PasswordChanged` 핸들러 → `vm.Password`
- "로그인" Button → `{Binding LoginCommand}`
- 로그인 상태 TextBlock → `{Binding LoginStatus}`
- 기존 "인증 토큰" PasswordBox(line 246-250): 로그인 성공 시 자동 채워지므로 **읽기전용 표시** 또는 제거. 수동 토큰 입력은 폴백으로 유지 가능(판단: 유지하되 라벨을 "토큰(자동)"으로 — UIDesigner 재량).

**기존 레이아웃 인자 영향:** AuthScheme 콤보가 있던 2-컬럼 Grid(line 226-252)는 인증 토큰 단독 또는 로그인 그룹으로 재배치.

### SettingsWindow.xaml.cs — Password 푸시 핸들러 추가

- `AuthTokenBox_PasswordChanged`(line 46-50) 패턴 복제하여 `LoginPasswordBox_PasswordChanged` 추가:
  ```csharp
  private void LoginPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
  {
      if (DataContext is SettingsViewModel vm)
          vm.Password = LoginPasswordBox.Password;
  }
  ```
- 생성자(line 19) `AuthTokenBox.Password = vm.AuthToken;` 유지(토큰 시드). 토큰 박스를 읽기전용으로 두면 시드만.

### MainWindow.xaml / 기타 — 영향 없음

- `LastUsageText`(line 180-182) 바인딩은 문자열 표시이므로 VM 내부 계산만 바뀜 → **XAML 무변경**.
- `DisplayName`(line 82)은 WorkspaceHistoryEntry → **무관**.

---

## 작업 분담

### ServiceEngineer (Models + Services)
- `Models/Agent/Usage.cs` — 필드명 정렬 (+total_tokens).
- `Models/Agent/ModelInfo.cs` — 서버 스펙 필드 정렬.
- `Models/Agent/RequestMetadata.cs` — `workspace` → `workspace_root`.
- `Models/AppSettings.cs` — `ModelId` 기본값 `""`.
- `Models/Agent/LoginResult.cs` — **신규** DTO.
- `Services/ToolCallJsonConverter.cs` — **신규** 컨버터(요청 arguments string화 + 읽기 대칭).
- `Services/AgentJson.cs` — 컨버터 등록.
- `Services/IAgentApiClient.cs` — `LoginAsync` 시그니처 추가.
- `Services/AgentApiClient.cs` — `LoginAsync` 구현, `ApplyAuth` Bearer 고정, `content_delta` → `delta`, `tool_call` arguments string 방어 파싱, `LoginResponseDto` private record.
- `Services/SettingsService.cs` — 마이그레이션의 `corp-llm-32b` 시드 제거.
- `Services/AgentOrchestrator.cs` — (확인만) `BuildRequest`는 위치 인자라 무변경. usage `Usage(0,0)` 컴파일 확인.

### ViewModelEngineer (ViewModels)
- `ViewModels/SettingsViewModel.cs` — `Username`/`Password`/`LoginStatus`/`IsLoggedIn` 추가, `LoginCommand`(`LoginAsync`) 구현, 토큰 영속, `AuthScheme`/`AuthSchemes` 제거, `SaveServerConfigAsync`에 `"Bearer"` 리터럴.
- `ViewModels/AgentSessionViewModel.cs` — line 661 `InputTokens`/`OutputTokens` → `PromptTokens`/`CompletionTokens`.

### UIDesigner (Views)
- `Views/SettingsWindow.xaml` — AuthScheme ComboBox 제거, 로그인 그룹(ID TextBox + PasswordBox + 로그인 버튼 + 상태 TextBlock) 추가, 토큰 박스 표시 정책 결정.
- `Views/SettingsWindow.xaml.cs` — `LoginPasswordBox_PasswordChanged` 핸들러 추가.

> **순서/병렬성**: ServiceEngineer가 `IAgentApiClient.LoginAsync` + `LoginResult` + `Usage`/`ModelInfo` 시그니처를 먼저 확정(인터페이스 우선). 확정 후 ViewModelEngineer·UIDesigner 병렬 진행.

---

## 호환성 깨짐 방지 주의사항

1. **ITool 구현 절대 불변**: 모든 `ITool`은 `ToolCall.Arguments`(JsonElement **객체**)를 받아 `GetProperty`로 파싱. 응답 파싱(3a)은 항상 객체를 만들고, 요청 직렬화(3b) 컨버터는 객체→문자열을 **직렬화 시점에만** 수행 → ITool이 보는 인메모리 표현은 항상 객체. `Services/Tools/*` 전부 무수정.
2. **SSE 파서 구조 유지**: `AgentApiClient.SendAsync`의 라인-바이-라인 `event:`/`data:` 파서, `Dispatch` 스위치 구조는 그대로. 변경은 `delta` 필드명과 tool_call arguments 분기 **내부**에 국한.
3. **`AgentJson.Options` 단일 직렬화 경로**: 요청 직렬화는 `SendAsync`의 `JsonSerializer.Serialize(request, AgentJson.Options)` 한 곳. 컨버터를 Options에 등록하면 `AgentMessage.ToolCalls` 안의 모든 ToolCall이 자동으로 arguments-string 형식. 어트리뷰트 방식과 **중복 등록 금지**(둘 중 하나).
4. **Usage 생성자 arity**: `total_tokens`를 기본값 `= 0` 인자로 추가하면 기존 `new Usage(0, 0)` 호출이 깨지지 않음. 비-기본값 3-인자로 만들면 `AgentApiClient`의 2개 호출(line 197-198 영역) 동반 수정 필요.
5. **로그인 토큰 영속 책임 분리**: `AgentApiClient.LoginAsync`는 토큰을 **저장하지 않고** 반환만. 저장은 VM이 `ISettingsService.UpdateServerConfigAsync`로 수행 → 단일 영속 경로 유지, `SettingsChanged` 이벤트도 정상 발화(다음 요청에 새 토큰 적용).
6. **LoginAsync는 ApplyAuth 호출 금지**: Public 엔드포인트. 기존 토큰을 Authorization에 실으면 서버가 무시하거나 오동작할 수 있으니 부착하지 않는다.
7. **PasswordBox 비바인딩 패턴 준수**: 기존 `AuthTokenBox` 코드비하인드 푸시 패턴 그대로 로그인 PasswordBox에 적용(MVVM 바인딩 불가 회피).

---

## DI 배선 영향 (App.xaml.cs)

- **변경 불필요**. `SettingsViewModel`은 이미 `(_settingsService, _api)`로 생성됨(line 181). `IAgentApiClient`에 메서드만 추가되고 생성자 시그니처는 불변.
- `AgentApiClient`는 `(HttpClient, ISettingsService)` 그대로. `LoginAsync`가 `httpClient`/`settings`만 사용.
- `SettingsViewModel.InitializeAsync`(모델 로드)는 현재 SettingsWindow 표시 시 호출 여부 확인 필요 — `App.xaml.cs` line 181-185는 `InitializeAsync`를 호출하지 않음. 로그인 상태 초기화(`IsLoggedIn = !string.IsNullOrWhiteSpace(AuthToken)`, `LoginStatus`)는 **생성자**에서 세팅 권장(별도 초기화 호출 없이).

---

## 구현 제외 범위

- 멤버/Provider/관리 API(`/members`, `/llm-providers`, `/me`, `/statistics` 등)·어드민 웹·세션 동기화(`/agent/sessions`)·suggestions·attachments 와이어 변경은 **이번 범위 밖**.
- JWT 만료·자동 재로그인·리프레시 토큰 처리(서버 스펙에 리프레시 미정의) — 미구현. 401 발생 시 사용자가 재로그인.
- `UpdateServerConfigAsync`의 `scheme` 파라미터 제거 리팩토링(시그니처 변경) — 파급 최소화 위해 유지, 항상 `"Bearer"` 전달.
- 에러 envelope 두 종류(평면 vs 중첩) 통합 처리 — 현 `ReadErrorAsync`(중첩 `error.code/message`)가 계약 API용으로 충분. `/auth/login`은 평면 envelope일 수 있으나 `ReadErrorAsync`가 실패 시 HTTP 상태 기반 폴백하므로 메시지 표시에 지장 없음(필요 시 LoginAsync에서 평면 `code/message`도 시도 — 선택 구현).
