# 03 — ViewModel Summary (ViewModelEngineer 산출물)

서버 불변, 클라이언트 ViewModel 레이어만 수정. 빌드 성공 (신규 오류 0, 기존 경고만 잔존).

## 변경 파일

| 파일 | 변경 |
|------|------|
| `OhMyAgent.AiAgent.Client/ViewModels/SettingsViewModel.cs` | 로그인(JWT) 추가, `AuthScheme`/`AuthSchemes` 제거, `SaveServerConfigAsync` "Bearer" 리터럴화 |
| `OhMyAgent.AiAgent.Client/ViewModels/AgentSessionViewModel.cs` | line 661 토큰 표시 `InputTokens`/`OutputTokens` → `PromptTokens`/`CompletionTokens` |

> 실제 파일 경로는 중첩 디렉터리: `.../OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client/ViewModels/`

---

## SettingsViewModel — 바인딩 계약 (UIDesigner용)

### 신규 추가 멤버

| 멤버 | 타입 | 바인딩 종류 | 용도 |
|------|------|------------|------|
| `Username` | `string` (`[ObservableProperty]`) | `{Binding Username}` (TwoWay) | 로그인 ID TextBox |
| `Password` | `string` (일반 public get/set, **ObservableProperty 아님**) | **바인딩 금지** — PasswordBox 코드비하인드 푸시 | 로그인 PW |
| `LoginStatus` | `string` (`[ObservableProperty]`) | `{Binding LoginStatus}` (OneWay) | 상태 TextBlock ("로그인됨"/"로그인 중..."/"실패: ...") |
| `IsLoggedIn` | `bool` (`[ObservableProperty]`) | `{Binding IsLoggedIn}` | UI 게이팅(선택) |
| `LoginCommand` | `AsyncRelayCommand` (`[RelayCommand] LoginAsync`) | `{Binding LoginCommand}` | 로그인 버튼 Command |

### 제거된 멤버 (XAML에서 반드시 제거할 것)

- `AuthScheme` (string `[ObservableProperty]`) — 삭제됨
- `AuthSchemes` (`IReadOnlyList<string>`) — 삭제됨
- → `SettingsWindow.xaml`의 **AuthScheme ComboBox** (`{Binding AuthSchemes}` / `{Binding AuthScheme}`) 바인딩 전부 제거 필요. 남겨두면 바인딩 오류 발생.

### 유지된 멤버

- `ServerBaseUrl` (`{Binding ServerBaseUrl}`)
- `AuthToken` (`{Binding AuthToken}`) — 로그인 성공 시 자동 채워짐. 수동 입력 PasswordBox는 유지/읽기전용 정책은 UIDesigner 재량.
- `ModelId`, `MaxIterations`, `MaxTokens`, `AvailableModels`
- `SaveServerConfigCommand`, `LoadModelsCommand`

---

## 로그인 UI 필요 요소 (SettingsWindow.xaml — 서버 설정 카드 내)

1. **사용자 ID TextBox** → `Text="{Binding Username, UpdateSourceTrigger=PropertyChanged}"`
2. **비밀번호 PasswordBox** → `x:Name="LoginPasswordBox"`, `PasswordChanged="LoginPasswordBox_PasswordChanged"`
   - 코드비하인드 핸들러에서 `vm.Password = LoginPasswordBox.Password;` 푸시 (기존 `AuthTokenBox_PasswordChanged` 패턴 동일).
   - **로그인 성공 시 VM이 `Password = ""`로 비우므로**, 보안상 코드비하인드에서 PasswordBox.Clear() 동기화는 선택 사항.
3. **로그인 Button** → `Command="{Binding LoginCommand}"`
4. **로그인 상태 TextBlock** → `Text="{Binding LoginStatus}"`

> AuthScheme ComboBox가 있던 2-컬럼 Grid는 인증 토큰 단독 또는 로그인 그룹으로 재배치.

---

## LoginAsync 동작 흐름 (참고)

```
LoginStatus = "로그인 중...";
result = await _api.LoginAsync(Username, Password);   // IAgentApiClient (ServiceEngineer 구현, 시그니처 확인됨)
성공 → AuthToken = result.Token;
       _settings.UpdateServerConfigAsync(ServerBaseUrl, "Bearer", AuthToken, ModelId, MaxIterations, MaxTokens);  // 단일 영속 경로
       IsLoggedIn = true; LoginStatus = "로그인됨"; Password = "";
실패 → IsLoggedIn = false; LoginStatus = $"실패: {result.ErrorMessage}";
```

- 토큰 영속은 VM 책임(설계서 §호환성5). 클라이언트(`LoginAsync`)는 결과만 반환.
- 생성자에서 저장된 토큰 유무로 `IsLoggedIn`/`LoginStatus` 초기화(별도 InitializeAsync 호출 불필요).

## AgentSessionViewModel

- `LastUsageText` 표시 로직만 변경 (`in:{PromptTokens} out:{CompletionTokens}`). 바인딩 이름/타입 불변 → **XAML 무변경**.

## 의존 시그니처 확인 결과 (ServiceEngineer 산출물과 정합)

- `IAgentApiClient.LoginAsync(string username, string password, CancellationToken ct = default)` → `Task<LoginResult>` ✅ 존재 확인
- `LoginResult(bool Success, string? Token, string? ErrorMessage)` ✅ 존재 확인
- `Usage(PromptTokens, CompletionTokens, TotalTokens=0)` ✅ 존재 확인
- `ISettingsService.UpdateServerConfigAsync(baseUrl, scheme, token, modelId, maxIterations, maxTokens)` ✅ 시그니처 불변
