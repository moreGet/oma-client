# 04. UI Summary - MCP Wiring & Status Display

## 작업 개요
MCP RemoteAgentService를 App.xaml.cs에 연결하고 MainWindow에 MCP 서버 상태 인디케이터를 추가했다. 또한 SettingsService에 SchemaVersion 마이그레이션 로직을 추가했다.

## 수정한 파일 목록

### 1. `App.xaml.cs`
경로: `/mnt/c/Users/dkdlw/RiderProjects/OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client/App.xaml.cs`

변경:
- 필드 추가: `private IRemoteAgentService? _mcpService;`
- `OnStartup`에서 `McpRemoteAgentService` 인스턴스 생성 (생성자: `settings`, `sseServer`, `executor` — 실제 파라미터명은 `executor`이며, 작업지시서의 `scriptExecutor`가 아님에 주의)
- `MainViewModel` 생성자에 `_mcpService` 4번째 인자 전달
- `_mainVm.InitializeAsync()` 직후 `_settingsService!.Current.McpEnabled` 가 true이면 `_mcpService.StartAsync()` 호출 (fire-and-forget)
- `OnExit`를 `async void`로 변경, MCP 서비스의 `StopAsync` → `DisposeAsync` 순으로 비동기 정리. try/catch로 예외 격리.

### 2. `MainWindow.xaml`
경로: `/mnt/c/Users/dkdlw/RiderProjects/OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client/MainWindow.xaml`

변경:
- 상태/도메인 바(Grid Row="1") 좌측의 연결 상태 StackPanel 내부, 기존 `StatusText` TextBlock 다음에 다음 요소 추가:
  - 구분자 ` | ` (TextMuted, FontSize 13)
  - 6x6 Ellipse — `IsMcpRunning`을 `BoolToStatusBrush` 컨버터로 색상 바인딩
  - `McpStatusText` TextBlock (TextSecondary, FontSize 12)

### 3. `Services/SettingsService.cs`
경로: `/mnt/c/Users/dkdlw/RiderProjects/OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client/Services/SettingsService.cs`

변경:
- `LoadAsync()`를 `async Task`로 변경 (인터페이스 시그니처 `Task LoadAsync()`와 호환)
- 내부 `Task.Run` 람다가 `migrationNeeded` 플래그(bool)를 반환하도록 변경
- JSON 파싱 성공 후 `Current.SchemaVersion < 2`이면 `McpPort=3000`, `McpEnabled=true`, `SchemaVersion=2`로 업그레이드 후 `migrationNeeded = true`
- `Task.Run` 종료 후 락 외부에서 `await SaveAsync()` 호출하여 재진입 데드락 방지

## QAReviewer가 확인해야 할 사항

### 컴파일 / 정합성
- [ ] `App.xaml.cs`의 `McpRemoteAgentService` 생성자 호출에서 named parameter `executor:`가 실제 `McpRemoteAgentService.cs`의 파라미터명과 일치하는지 (작업지시서는 `scriptExecutor`였으나 실제 코드는 `executor`임)
- [ ] `MainViewModel` 생성자에 `IRemoteAgentService?` 4번째 인자가 정상적으로 매칭되는지
- [ ] `OnExit`의 `async void`가 WPF 패턴상 허용되는 형태인지 (Application life-cycle 메서드라 허용)
- [ ] `ISettingsService.LoadAsync()` 시그니처(`Task LoadAsync()`)와 구현 `async Task LoadAsync()`가 호환되는지

### 런타임 / 동작
- [ ] `_mcpService.StartAsync()`가 fire-and-forget인데 예외 발생 시 unobserved task가 되어도 괜찮은지 (`McpRemoteAgentService` 내부에서 Debug.WriteLine으로 로깅하고 `RunningStateChanged(false)` 발생 후 throw — fire-and-forget이라 throw가 surface 되지 않음. ViewModel은 이벤트로 상태 동기화 가능)
- [ ] `OnExit`에서 `StopAsync` 후 `DisposeAsync`를 또 호출 — `DisposeAsync`가 내부적으로 다시 `StopAsync`를 호출해도 `_isRunning` 플래그로 idempotent하므로 문제없음
- [ ] 앱 시작 시 settings 파일이 없으면 새 `AppSettings`(기본 SchemaVersion=2) 생성 → 마이그레이션 분기 미실행 (정상)
- [ ] 기존 사용자(SchemaVersion 0/1 파일)가 있으면 LoadAsync 도중 자동 마이그레이션 → SaveAsync 호출 (락 밖에서 호출하므로 데드락 없음)

### XAML 바인딩
- [ ] `IsMcpRunning`, `McpStatusText`는 `MainViewModel`의 `[ObservableProperty]`로 이미 정의되어 있음 (소스 제너레이터로 PropertyChanged 발생)
- [ ] `BoolToStatusBrush` 컨버터가 6px 작은 Ellipse에서도 시각적으로 식별 가능한지 (디자인 검토)
- [ ] 좁은 창 폭에서 도메인 셀렉터와 좌측 상태 영역이 겹치지 않는지

### 부차 확인
- [ ] `OnStartup`에서 MCP 시작 실패 시 메인 UI는 계속 동작 (단순히 IsMcpRunning=false 상태로 유지)
- [ ] Tray의 `ExitApplication` 경로에서 `Shutdown()` → `OnExit` 흐름이 동작하여 `_mcpService.StopAsync()`가 실제로 호출되는지
