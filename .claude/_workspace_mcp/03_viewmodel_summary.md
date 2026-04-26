# 03. ViewModel 요약 — MainViewModel MCP 상태 표시 통합

## 변경 파일
- `OhMyAgent.AiAgent.Client/ViewModels/MainViewModel.cs`

## 변경 내용 요약

### 1. 의존성 필드 추가
```csharp
private readonly IRemoteAgentService? _mcpService;
```
- nullable 필드로, MCP 서비스가 주입되지 않아도 동작하도록 안전 처리.
- `using OhMyAgent.AiAgent.Client.Services;`는 기존에 이미 선언되어 있어 추가 import 불필요.

### 2. 생성자 시그니처 확장 (옵셔널 파라미터 방식)
```csharp
public MainViewModel(IChatService chatService,
                     IAgentActionService agentActionService,
                     ISettingsService settingsService,
                     IRemoteAgentService? mcpService = null)
```
- 기존 호출부(3-인자) 호환성 유지: `mcpService = null` 기본값.
- `_mcpService != null`인 경우에만:
  - `RunningStateChanged` 이벤트 구독
  - 초기 상태(`IsRunning`, `Port`)로 백킹 필드 초기화
- 미주입 시 기본값 `IsMcpRunning = false`, `McpStatusText = "MCP 서버 비활성"` 유지.

### 3. ObservableProperty 추가
```csharp
[ObservableProperty] private bool   _isMcpRunning;
[ObservableProperty] private string _mcpStatusText = "MCP 서버 비활성";
```
CommunityToolkit.Mvvm 소스 생성기를 통해 다음이 자동 생성됨:
- `IsMcpRunning` (bool)
- `McpStatusText` (string)
- `INotifyPropertyChanged` 통지 포함.

### 4. 이벤트 핸들러 / 헬퍼
```csharp
private void OnMcpRunningStateChanged(object? sender, bool isRunning)
{
    IsMcpRunning  = isRunning;
    McpStatusText = GetMcpStatusText(isRunning, _mcpService?.Port ?? 0);
}

private static string GetMcpStatusText(bool isRunning, int port)
    => isRunning ? $"MCP :{port}" : "MCP 오프";
```
- 실행 중: `MCP :<port>` (예: `MCP :7777`)
- 비실행: `MCP 오프`

## UIDesigner 바인딩 대상 프로퍼티

| 프로퍼티 | 타입 | 용도 | 추천 바인딩 위치 |
|----------|------|------|------------------|
| `IsMcpRunning` | `bool` | MCP 서버 실행 상태 (인디케이터 색상/아이콘 토글) | StatusBar 내 인디케이터(타원/도트), `Foreground` 또는 `Visibility` |
| `McpStatusText` | `string` | MCP 상태 라벨 (`MCP :<port>` 또는 `MCP 오프` / `MCP 서버 비활성`) | StatusBar의 `TextBlock.Text` |

### 권장 바인딩 패턴 (참고용)
```xml
<StackPanel Orientation="Horizontal" Margin="8,0">
    <Ellipse Width="8" Height="8" Margin="0,0,4,0">
        <Ellipse.Style>
            <Style TargetType="Ellipse">
                <Setter Property="Fill" Value="{StaticResource MutedBrush}"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsMcpRunning}" Value="True">
                        <Setter Property="Fill" Value="{StaticResource SuccessBrush}"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Ellipse.Style>
    </Ellipse>
    <TextBlock Text="{Binding McpStatusText}" VerticalAlignment="Center"/>
</StackPanel>
```

## 호환성 / 영향 범위
- 기존 3-인자 생성자 호출(`new MainViewModel(chat, action, settings)`)은 그대로 동작 (옵셔널 파라미터).
- DI 컨테이너에서 `IRemoteAgentService`가 등록되어 있으면 자동 주입됨.
- 미등록 환경에서는 nullable이므로 NRE 없이 정상 동작.

## 의존 전제
- `IRemoteAgentService` 인터페이스가 `OhMyAgent.AiAgent.Client.Services` 네임스페이스에 정의되어야 하며 다음 멤버를 노출해야 함:
  - `bool IsRunning { get; }`
  - `int Port { get; }`
  - `event EventHandler<bool> RunningStateChanged;`
- (Phase 2A 산출물에서 정의된 것으로 가정)
