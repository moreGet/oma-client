# 01. Architect Spec

본 문서는 다음 3개 요구사항에 대한 구현 설계입니다.

1. X 버튼 → 종료 확인 다이얼로그 + 완전 종료
2. MainWindow 타이틀바에 트레이 최소화 전용 버튼 추가
3. MainWindow가 보이고 있는 동안 글로벌 핫키(Ctrl+Space) 차단

---

## 수정 파일 목록

| 파일 | 변경 유형 | 핵심 내용 |
|------|----------|----------|
| `OhMyAgent.AiAgent.Client/App.xaml.cs` | 가시성 변경 | `ExitApplication()` 를 `internal` 로 노출 (창에서 호출 가능하도록) |
| `OhMyAgent.AiAgent.Client/MainWindow.xaml` | XAML 수정 | 타이틀바 우측 버튼 그룹에 트레이 버튼 추가 (최소화 ← 트레이 ← X 순) |
| `OhMyAgent.AiAgent.Client/MainWindow.xaml.cs` | 핸들러 재작성 | `MinimizeButton_Click`, `TrayButton_Click`(신규), `CloseButton_Click`, `OnClosing` 모두 새 시맨틱으로 교체 |
| `OhMyAgent.AiAgent.Client/Views/ChatOnlyWindow.xaml.cs` | 핸들러 수정 | `CloseButton_Click`, `OnClosing` 종료 확인 다이얼로그 추가 (단, ChatOnly는 원래 Hide 동작이 유지되어야 하므로 정책 결정 필요 → 아래 § 정책 결정 참조) |
| `OhMyAgent.AiAgent.Client/Services/ChatWindowCoordinator.cs` | 가드 추가 | `ToggleChatOnly()` 진입 시 MainWindow.IsVisible 검사 → true면 즉시 return |
| `OhMyAgent.AiAgent.Client/Services/IChatWindowCoordinator.cs` | 변경 없음 | 시그니처 동일 |

---

## 정책 결정 (요구사항 1번 적용 범위)

요구사항 1번은 "MainWindow와 ChatOnlyWindow의 X 버튼 클릭 시" 종료 확인을 띄우라고 명시되어 있습니다.

- **현재 동작**: `ChatOnlyWindow.CloseButton_Click` → `Hide()`. 즉, ChatOnly의 X 버튼은 "닫기 (Esc와 동일, 숨김)" 의미였음 (`ToolTip="닫기 (Esc)"` 명시됨).
- **요구사항 적용**: 요구사항 1번을 문자 그대로 따르면 ChatOnlyWindow의 X 버튼도 "프로그램 종료 확인" 다이얼로그를 띄워야 함. 본 설계는 **요구사항을 그대로 따른다**.
  - 단, ChatOnlyWindow의 `OnKeyDown(Esc)` 와 `Hide` 시맨틱은 별도 동작으로 유지(Esc는 단순 숨김).
  - X 버튼과 OnClosing은 종료 확인 다이얼로그를 띄움.

> **Note**: 만약 사용자가 "ChatOnly의 X는 단순 숨김 유지"를 원한다면, 본 설계 § ChatOnlyWindow.xaml.cs 섹션의 핸들러를 기존 `Hide()` 호출로 되돌리기만 하면 됩니다. 본 설계는 요구사항 명세를 우선으로 합니다.

---

## 변경 상세

### App.xaml.cs

#### 변경 1: `ExitApplication()` 가시성 변경

기존:
```csharp
private void ExitApplication()
```

변경:
```csharp
internal void ExitApplication()
```

이유: `MainWindow.CloseButton_Click` 및 `OnClosing`, `ChatOnlyWindow.CloseButton_Click` 등에서 호출 필요. 같은 어셈블리이므로 `internal` 로 충분.

#### 변경 없음 (그대로 유지)

- `HideToTray(Window window)` — 그대로 유지. 트레이 전용 버튼이 호출함.
- `IsExiting` 정적 프로퍼티 — 그대로 유지. 종료 확인 후 `ExitApplication()` 가 `IsExiting = true` 로 세팅하므로 OnClosing 가드가 정상 동작.
- `OnExit(ExitEventArgs)` — 그대로 유지.

---

### MainWindow.xaml

#### 변경 위치
타이틀바 우측의 버튼 `StackPanel` (라인 114~161) 내부, `Slider` 다음에 위치한 두 개의 `Button` (최소화 `—`, 종료 `✕`) 사이에 트레이 버튼을 신규 추가.

#### 정확한 XAML 스니펫 (교체 대상: 라인 132~160)

기존 (최소화 버튼 + X 버튼):
```xml
<Button Content="—"
        Click="MinimizeButton_Click"
        Width="36" Height="28"
        FontSize="16"
        Background="Transparent"
        BorderThickness="0"
        Foreground="{StaticResource TextSecondary}"
        Cursor="Hand"
        VerticalContentAlignment="Bottom"
        Padding="0,0,0,4"/>
<Button Content="✕"
        Click="CloseButton_Click"
        ...>
    ...
</Button>
```

변경 후 (최소화 → 트레이 → X 순서):
```xml
<!-- 최소화 (작업표시줄로) -->
<Button Content="—"
        Click="MinimizeButton_Click"
        Width="36" Height="28"
        FontSize="16"
        Background="Transparent"
        BorderThickness="0"
        Foreground="{StaticResource TextSecondary}"
        Cursor="Hand"
        VerticalContentAlignment="Bottom"
        Padding="0,0,0,4"
        ToolTip="최소화"/>

<!-- 트레이로 숨기기 (신규) -->
<Button Content="🔽"
        Click="TrayButton_Click"
        Width="36" Height="28"
        FontSize="13"
        Background="Transparent"
        BorderThickness="0"
        Foreground="{StaticResource TextSecondary}"
        Cursor="Hand"
        ToolTip="트레이로 숨기기"/>

<!-- 종료 -->
<Button Content="✕"
        Click="CloseButton_Click"
        Width="36" Height="28"
        FontSize="12"
        Background="Transparent"
        BorderThickness="0"
        Foreground="{StaticResource TextSecondary}"
        Cursor="Hand"
        ToolTip="종료">
    <Button.Style>
        <Style TargetType="Button">
            <Style.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter Property="Background" Value="#F85149"/>
                    <Setter Property="Foreground" Value="White"/>
                </Trigger>
            </Style.Triggers>
        </Style>
    </Button.Style>
</Button>
```

#### 시각적 순서 (좌 → 우)
`[투명도 슬라이더] [—] [🔽] [✕]`

---

### MainWindow.xaml.cs

#### 사용 네임스페이스 추가 (필요 시)
- `System.Windows.MessageBox` 는 `System.Windows` 네임스페이스. 이미 `using System.Windows;` 있음 → 추가 import 불필요.
- 단, 사용 시 `MessageBox.Show(...)` 호출에서 `System.Windows.Forms.MessageBox` 와 충돌하지 않도록 주의 (현재 파일은 WinForms를 import하지 않으므로 안전).

#### 변경 1: `MinimizeButton_Click` (라인 42~43)

기존:
```csharp
// 최소화 → 트레이로
private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    => ((App)Application.Current).HideToTray(this);
```

변경:
```csharp
// 최소화 → 작업표시줄로 (트레이로 가지 않음)
private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    => WindowState = WindowState.Minimized;
```

#### 변경 2: `TrayButton_Click` 신규 추가 (CloseButton_Click 위에 배치)

```csharp
// 트레이로 숨기기 (신규 전용 버튼)
private void TrayButton_Click(object sender, RoutedEventArgs e)
    => ((App)Application.Current).HideToTray(this);
```

#### 변경 3: `CloseButton_Click` (라인 46~47)

기존:
```csharp
// X 버튼 → 트레이로 (종료 아님)
private void CloseButton_Click(object sender, RoutedEventArgs e)
    => ((App)Application.Current).HideToTray(this);
```

변경:
```csharp
// X 버튼 → Window.Close() 호출 → OnClosing이 종료 확인 다이얼로그를 책임짐
private void CloseButton_Click(object sender, RoutedEventArgs e)
    => Close();
```

> **설계 핵심**: 다이얼로그 표시 로직을 `OnClosing` 한 곳에 집중. `CloseButton_Click` 은 `Close()` 만 호출하면 자동으로 `OnClosing` 이 발화되어 단일 진입점이 됨. → MessageBox 중복 방지(§ 주의사항 참조).

#### 변경 4: `OnClosing` (라인 50~58)

기존:
```csharp
protected override void OnClosing(CancelEventArgs e)
{
    if (!App.IsExiting)
    {
        e.Cancel = true;
        ((App)Application.Current).HideToTray(this);
    }
    base.OnClosing(e);
}
```

변경:
```csharp
protected override void OnClosing(CancelEventArgs e)
{
    // App.IsExiting (트레이 메뉴 → Exit 등) 인 경우 다이얼로그 없이 즉시 종료
    if (App.IsExiting)
    {
        base.OnClosing(e);
        return;
    }

    var result = MessageBox.Show(
        this,
        "프로그램을 종료하시겠습니까?",
        "OhMyAgent",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question,
        MessageBoxResult.No);

    if (result == MessageBoxResult.Yes)
    {
        // 완전 종료
        ((App)Application.Current).ExitApplication();
        // ExitApplication() 내부에서 IsExiting=true → Shutdown() → 모든 창 OnClosing 재발화 시 위 가드로 통과
        // 이 OnClosing 자체는 e.Cancel=false 그대로 두어 정상 종료 흐름 진행
    }
    else
    {
        // 취소 → 창 유지
        e.Cancel = true;
    }

    base.OnClosing(e);
}
```

> **흐름 분석**:
> - 사용자가 X 버튼 클릭 → `CloseButton_Click` → `Close()` → `OnClosing` 발화 → 다이얼로그
> - Yes → `ExitApplication()` → `IsExiting = true` → `Shutdown()` 호출
> - `Shutdown()` 은 모든 Window의 Close를 트리거 → MainWindow와 ChatOnlyWindow의 `OnClosing` 이 다시 호출됨 → 그러나 이미 `IsExiting == true` 이므로 가드를 통해 즉시 통과 (다이얼로그 재표시 없음)

#### 변경 없음
- `OnSourceInitialized` — 그대로 유지
- `InputBox_KeyDown`, `Messages_CollectionChanged`, `TitleBar_MouseLeftButtonDown` — 그대로 유지

---

### ChatOnlyWindow.xaml.cs

#### 변경 1: `CloseButton_Click` (라인 29~30)

기존:
```csharp
private void CloseButton_Click(object sender, RoutedEventArgs e)
    => Hide();
```

변경:
```csharp
private void CloseButton_Click(object sender, RoutedEventArgs e)
    => Close();
```

#### 변경 2: `OnClosing` (라인 51~59)

기존:
```csharp
protected override void OnClosing(CancelEventArgs e)
{
    if (!App.IsExiting)
    {
        e.Cancel = true;
        Hide();
    }
    base.OnClosing(e);
}
```

변경:
```csharp
protected override void OnClosing(CancelEventArgs e)
{
    if (App.IsExiting)
    {
        base.OnClosing(e);
        return;
    }

    var result = System.Windows.MessageBox.Show(
        this,
        "프로그램을 종료하시겠습니까?",
        "OhMyAgent",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question,
        MessageBoxResult.No);

    if (result == MessageBoxResult.Yes)
    {
        ((App)System.Windows.Application.Current).ExitApplication();
    }
    else
    {
        e.Cancel = true;
    }

    base.OnClosing(e);
}
```

#### 변경 없음
- `OnKeyDown(Esc)` 의 `Hide()` 호출 — 그대로 유지. Esc 는 "단순 숨김(트레이 핫키로 다시 열 수 있음)" 의미를 보존.
- `InputBox_KeyDown`, `Messages_CollectionChanged`, `TitleBar_MouseLeftButtonDown` — 그대로 유지.

---

### ChatWindowCoordinator.cs

#### 변경: `ToggleChatOnly()` 메서드 — MainWindow 활성 가드 추가 (라인 25~45)

기존:
```csharp
public void ToggleChatOnly()
{
    if (_chatOnlyWindow == null)
    {
        _chatOnlyWindow = new ChatOnlyWindow(_getMainVm());
        _chatOnlyWindow.Closed += (_, _) => _chatOnlyWindow = null;
        _chatOnlyWindow.Show();
        _chatOnlyWindow.Activate();
        return;
    }

    if (!_chatOnlyWindow.IsVisible)
    {
        _chatOnlyWindow.Show();
        _chatOnlyWindow.Activate();
    }
    else
    {
        _chatOnlyWindow.Activate();
    }
}
```

변경:
```csharp
public void ToggleChatOnly()
{
    // 메인 창이 보이고 있으면 ChatOnly 토글 무시
    // (사용자가 메인 창을 사용 중인데 핫키로 별도 채팅 창이 뜨는 것을 방지)
    var main = _getMainWindow();
    if (main != null && main.IsVisible)
    {
        // 보이긴 하지만 다른 창에 가려져 있을 수 있으므로 활성화 정도만 수행할지 여부는 정책 선택
        // 요구사항 명세: "MainWindow가 보이고 있으면 ToggleChatOnly() 무시" → 완전 무시
        return;
    }

    if (_chatOnlyWindow == null)
    {
        _chatOnlyWindow = new ChatOnlyWindow(_getMainVm());
        _chatOnlyWindow.Closed += (_, _) => _chatOnlyWindow = null;
        _chatOnlyWindow.Show();
        _chatOnlyWindow.Activate();
        return;
    }

    if (!_chatOnlyWindow.IsVisible)
    {
        _chatOnlyWindow.Show();
        _chatOnlyWindow.Activate();
    }
    else
    {
        _chatOnlyWindow.Activate();
    }
}
```

> **Note**: `Window.IsVisible` 은 `Window.Show()` 호출되고 `Hide()/Close()` 가 안 되었으며 `WindowState.Minimized` 가 아닌 경우(작업표시줄 최소화 시에도 `IsVisible == true` 임)에 true. 즉, 작업표시줄에 최소화된 상태(요구사항 2번에서 도입한 새 최소화)에서는 `IsVisible == true` → 핫키 차단됨. 이 시맨틱이 사용자 의도와 맞는지 확인 필요. 만약 "최소화된 상태에서는 핫키로 ChatOnly를 띄울 수 있어야 한다"면 다음과 같이 변경:
> ```csharp
> if (main != null && main.IsVisible && main.WindowState != WindowState.Minimized)
>     return;
> ```
> 본 설계는 요구사항 명세("IsVisible == true 인 상태에서")를 그대로 따라 첫 번째 형태(WindowState 미체크)를 채택.

---

## 인터페이스 변경

### IChatWindowCoordinator.cs

**변경 없음.** `ToggleChatOnly()` 시그니처 동일. 메인 창 가드 로직은 구현체 내부에서 `_getMainWindow()` 팩토리를 통해 접근하므로 인터페이스 변경 불필요.

---

## 주의사항

### 1. MessageBox 중복 표시 방지 (가장 중요)

문제 시나리오: `CloseButton_Click` 에서도 다이얼로그를 띄우고, `OnClosing` 에서도 다이얼로그를 띄우면 X 버튼 1회 클릭으로 다이얼로그가 2번 발화됨.

**해결책 (본 설계 적용)**:
- `CloseButton_Click` 은 `Close()` 만 호출
- 다이얼로그 표시 + 사용자 의사 확인 + Cancel 처리는 모두 `OnClosing` 한 곳에서만 수행
- `Close()` 는 자동으로 `OnClosing` 을 발화하므로 X 버튼 / Alt+F4 / 코드의 `Close()` 호출 / `Application.Shutdown()` 모두 동일 코드 경로 사용

### 2. `ExitApplication()` → `Shutdown()` 재진입 방지

`ExitApplication()` 내부에서 `IsExiting = true` 후 `Shutdown()` 호출 → WPF가 모든 열린 창에 대해 Close를 시도 → 각 창의 `OnClosing` 재호출. 이 때 `App.IsExiting` 가드가 `true` 이므로:
- MainWindow.OnClosing → 즉시 `base.OnClosing(e)` 후 return → 정상 닫힘
- ChatOnlyWindow.OnClosing → 동일

따라서 다이얼로그 재표시 없이 깔끔하게 종료됨.

### 3. ChatOnlyWindow의 Esc / Hide 의미 보존

ChatOnly 창은 `Topmost=True`, `ShowInTaskbar=False` 인 보조 채팅 창. `Esc` 또는 핫키 재입력으로 빠르게 숨기는 용도가 보존되어야 함.
- `OnKeyDown(Esc) → Hide()` : 변경 없음 → Esc 는 종료 확인 없이 즉시 숨김
- `CloseButton_Click(X) → Close() → OnClosing → 종료 확인` : 명시적 종료 의사로 해석

이 둘의 의미가 분리되어 사용자가 "임시 숨김(Esc)" 과 "종료 확인(X)" 을 구분할 수 있음.

### 4. 트레이 버튼 아이콘 호환성

`🔽` 이모지는 Segoe UI Emoji 폰트가 필요. Windows 10/11 기본 탑재이므로 문제 없음. 만약 흑백 표시를 원한다면 `🗕` (U+1F5D5, Window Minimize) 또는 Segoe MDL2 Assets 의 `&#xE944;` (Tray) 코드포인트로 대체 가능. 본 설계는 `🔽` 채택.

### 5. `ExitApplication()` 호출 시점의 Dispose 순서

`App.ExitApplication()` 은 `_globalHotkey?.Dispose()`, `_trayIcon?.Dispose()` 후 `Shutdown()` 호출. `OnExit` 에서도 동일 dispose 시도하지만 이미 dispose된 객체는 null 체크로 안전. (현재 코드 그대로 OK).

### 6. 빌드/스레드 안전성

- `MessageBox.Show(this, ...)` 는 UI 스레드에서 호출되어야 하며, `OnClosing` 은 UI 스레드에서 호출되므로 안전.
- `ChatWindowCoordinator.ToggleChatOnly()` 는 `App.OnStartup` 에서 `Dispatcher.Invoke` 로 마샬링되어 호출됨 (라인 75) → UI 스레드 보장 → `Window.IsVisible` 접근 안전.

### 7. 회귀 테스트 체크리스트

- [ ] MainWindow X 버튼 클릭 → 다이얼로그 1회 표시 → No → 창 유지
- [ ] MainWindow X 버튼 클릭 → 다이얼로그 1회 표시 → Yes → 앱 완전 종료 (트레이 아이콘 사라짐, 프로세스 종료)
- [ ] MainWindow Alt+F4 → 다이얼로그 1회 표시
- [ ] MainWindow `—` 버튼 → 작업표시줄 최소화 (트레이로 안 감)
- [ ] MainWindow `🔽` 버튼 → 트레이로 숨김 + 풍선 힌트 표시
- [ ] MainWindow 보이는 상태에서 Ctrl+Space → ChatOnly 안 뜸
- [ ] MainWindow 트레이로 숨긴 상태에서 Ctrl+Space → ChatOnly 뜸
- [ ] ChatOnlyWindow X 버튼 → 종료 확인 다이얼로그 → Yes → 앱 완전 종료
- [ ] ChatOnlyWindow Esc → 다이얼로그 없이 즉시 숨김
- [ ] 트레이 메뉴 → Exit → 다이얼로그 없이 즉시 종료 (App.IsExiting 가드)
