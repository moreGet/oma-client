# 02. Service Layer 수정 요약

본 문서는 `01_architect_spec.md` 설계의 서비스 레이어 변경분 구현 결과를 요약합니다.

---

## 수정 파일

| # | 파일 | 변경 유형 | 라인 |
|---|------|----------|------|
| 1 | `OhMyAgent.AiAgent.Client/App.xaml.cs` | 가시성 변경 | 172 |
| 2 | `OhMyAgent.AiAgent.Client/Services/ChatWindowCoordinator.cs` | 가드 추가 | 25~30 |

---

## 1. App.xaml.cs

### 변경 내용
`ExitApplication()` 메서드의 접근 한정자를 `private` 에서 `internal` 로 변경.

### Before
```csharp
private void ExitApplication()
{
    IsExiting = true;
    _globalHotkey?.Dispose();
    _trayIcon?.Dispose();
    Shutdown();
}
```

### After
```csharp
internal void ExitApplication()
{
    IsExiting = true;
    _globalHotkey?.Dispose();
    _trayIcon?.Dispose();
    Shutdown();
}
```

### 이유
- `MainWindow.CloseButton_Click` / `OnClosing` 및 `ChatOnlyWindow.CloseButton_Click` / `OnClosing` 등 동일 어셈블리의 다른 창 코드에서 종료 확인 다이얼로그 결과 처리 후 호출 가능해야 함.
- `internal` 로 노출하여 동일 어셈블리 내 호출만 허용 (외부 노출 X) → 캡슐화 유지.

### 영향 범위
- 동작 변화 없음 (시그니처/로직 동일).
- 다른 창 클래스에서 `((App)Application.Current).ExitApplication()` 호출이 가능해짐.
- `InitializeTrayIcon()` 의 `exitItem.Click += (_, _) => ExitApplication();` 호출은 그대로 유지됨.

---

## 2. ChatWindowCoordinator.cs

### 변경 내용
`ToggleChatOnly()` 메서드 진입 직후 최상단에 MainWindow 가시성 가드 추가. 메인 창이 보이고 있으면 즉시 return하여 ChatOnly 토글 동작을 차단.

### Before (라인 25~45)
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

### After
```csharp
public void ToggleChatOnly()
{
    // 메인 창이 보이고 있으면 ChatOnly 토글 무시
    var main = _getMainWindow();
    if (main != null && main.IsVisible)
        return;

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

### 이유 (요구사항 3번 충족)
- 사용자가 MainWindow를 사용 중인 동안 글로벌 핫키(Ctrl+Space)로 별도의 ChatOnly 보조 창이 뜨면 UX가 혼란스러움.
- `App.OnStartup` 에서 `_globalHotkey.HotkeyPressed += (_, _) => Dispatcher.Invoke(_windowCoordinator.ToggleChatOnly);` 로 핫키와 토글이 연결되어 있으므로, 이 가드가 곧 핫키 차단 효과.

### 동작 시맨틱
- `Window.IsVisible` 은 `Show()` 호출 후 `Hide()/Close()` 가 안 되었을 때 `true`.
- 작업표시줄 최소화 상태(`WindowState.Minimized`)에서도 `IsVisible == true` → 이 경우에도 핫키 차단됨.
- 트레이로 숨긴 상태(`Hide()` 호출됨)에서는 `IsVisible == false` → 핫키 정상 동작 → ChatOnly 토글 가능.
- 설계 문서 § ChatWindowCoordinator.cs 의 정책 결정("WindowState 미체크")을 그대로 따름.

### 스레드 안전성
- `ToggleChatOnly()` 는 `App.OnStartup` 에서 `Dispatcher.Invoke` 로 마샬링된 후 호출됨 → UI 스레드 보장.
- `Window.IsVisible` 접근은 UI 스레드에서 안전.

---

## 미변경 항목

설계 문서 § 미변경 사항대로 다음은 손대지 않음:

- `App.xaml.cs`
  - `HideToTray(Window window)` — 트레이 전용 버튼이 호출함.
  - `IsExiting` 정적 프로퍼티 — `OnClosing` 가드용.
  - `OnExit(ExitEventArgs)` — dispose 흐름.
  - `RegisterMainWindowHwnd`, `OnStartup`, `InitializeTrayIcon`, `ShowMainWindow`, `CreateAppIcon` — 기존 동작 유지.
- `ChatWindowCoordinator.cs`
  - 생성자 / 필드 / `ShowMain()` — 변경 없음.
- `IChatWindowCoordinator.cs`
  - 시그니처 변경 없음 (가드 로직은 구현체 내부에서 `_getMainWindow()` 팩토리로 처리).

---

## 다음 단계 의존성

본 서비스 레이어 변경은 다음 후속 작업의 전제 조건입니다:

1. `MainWindow.xaml.cs` 의 `CloseButton_Click` / `OnClosing` 에서 `((App)Application.Current).ExitApplication()` 호출 가능 → 종료 확인 다이얼로그의 "Yes" 분기 구현 가능.
2. `ChatOnlyWindow.xaml.cs` 의 `OnClosing` 종료 확인 다이얼로그 "Yes" 분기에서 동일 호출 가능.
3. 글로벌 핫키(Ctrl+Space) 차단 동작은 `ChatWindowCoordinator.ToggleChatOnly()` 진입 가드만으로 완전 충족 → 핫키 서비스 자체 수정 불필요.

---

## 회귀 테스트 (서비스 레이어 한정)

- [ ] `ExitApplication()` 호출 시 `IsExiting=true` → `Shutdown()` → 모든 창 정상 종료 (트레이 메뉴 → Exit 경로로 검증 가능).
- [ ] MainWindow가 보이는 상태에서 Ctrl+Space → ChatOnly 안 뜸 (`ToggleChatOnly()` 가드 발동).
- [ ] MainWindow를 트레이로 숨긴 상태에서 Ctrl+Space → ChatOnly 토글 정상 동작.
- [ ] MainWindow를 작업표시줄로 최소화한 상태에서 Ctrl+Space → ChatOnly 안 뜸 (설계 명세대로).
