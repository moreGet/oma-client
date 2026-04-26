# 01_architect_spec.md — MainWindow 메뉴바 추가 설계

## 개요

`MainWindow` 최상단(타이틀바 위 또는 타이틀바 아래)에 다크 테마 메뉴바를 추가한다. 본 설계에서는 **타이틀바(현재 Row 0) 아래 / 상태바(현재 Row 1) 위**에 새로운 Row 0(이 아닌 Row 1로 메뉴 삽입)을 두는 것이 아닌, 요구사항대로 **최상단(Row 0)** 에 삽입한다.

> 단, `WindowStyle="None"` + 커스텀 타이틀바 구조에서 메뉴바를 타이틀바보다 위에 두면 드래그 영역과 시각적 위계가 어색해진다. 그러나 요구사항이 "최상단에 메뉴바 추가"이므로 **Row 0 = 메뉴바**, **Row 1 = 기존 타이틀바**, **Row 2 = 상태바**, **Row 3 = 채팅**, **Row 4 = 입력바** 순으로 재배치한다.
>
> (만약 시각적으로 타이틀바가 더 위에 있어야 한다면 메뉴바 RowDefinition을 Row 1로 삽입하고 기존 Row 1~3만 +1 하면 된다. 본 설계는 요구사항 텍스트의 "최상단" 표현을 그대로 따른다.)

---

## 1. 수정 파일 목록

| 파일 | 변경 요약 |
|------|-----------|
| `OhMyAgent.AiAgent.Client/App.xaml.cs` | `internal ISettingsService SettingsService => _settingsService!;` 프로퍼티 노출 |
| `OhMyAgent.AiAgent.Client/MainWindow.xaml` | RowDefinition 1개 추가(Auto), 기존 4개 Row의 `Grid.Row` 값 +1, Row 0에 `<Menu>` 삽입, 인라인 다크 스타일 |
| `OhMyAgent.AiAgent.Client/MainWindow.xaml.cs` | `MenuItem_HotkeySettings_Click`, `MenuItem_Exit_Click` 두 핸들러 추가. `using` 별칭 정리 |
| `OhMyAgent.AiAgent.Client/Resources/Styles.xaml` | (선택) Menu/MenuItem 다크 스타일 — 본 설계에서는 **MainWindow.xaml 내 인라인 스타일**로 처리하여 생략 가능 |

---

## 2. App.xaml.cs 변경

### 2-1. SettingsService 프로퍼티 노출

`_settingsService` 필드(line 23) 바로 아래, 또는 `IsExiting` 정적 프로퍼티(line 18) 바로 아래의 인스턴스 멤버 영역에 다음 프로퍼티를 추가한다.

**추가 위치:** `App.xaml.cs` line 28(`IRemoteAgentService? _mcpService;`) 다음 줄, `OnStartup` 메서드 위.

```csharp
    private IRemoteAgentService?      _mcpService;

    /// <summary>
    /// MainWindow 등 외부 코드에서 설정 서비스에 접근하기 위한 프로퍼티.
    /// OnStartup 이후에만 안전하게 접근 가능. 호출 측에서 null 검사 필수.
    /// </summary>
    internal ISettingsService SettingsService => _settingsService!;

    protected override async void OnStartup(StartupEventArgs e)
    { ... }
```

> `_settingsService`는 `OnStartup`에서 비동기 로드되므로, MainWindow 핸들러는 OnStartup 이후에만 호출됨이 보장된다. 따라서 `!` 단언은 안전하다. 다만 트레이 아이콘에서도 동일 패턴(`_settingsService == null` 가드)을 사용하고 있으므로, 안전성을 더 높이려면 핸들러 측에서도 null 가드를 한다(아래 4번 핸들러 참고).

---

## 3. MainWindow.xaml 변경

### 3-1. RowDefinitions 변경 (line 97~102 교체)

**기존:**
```xml
<Grid.RowDefinitions>
    <RowDefinition Height="40"/>   <!-- 타이틀바 -->
    <RowDefinition Height="56"/>   <!-- 상태/도메인 바 -->
    <RowDefinition Height="*"/>    <!-- 채팅 영역 -->
    <RowDefinition Height="Auto"/> <!-- 입력 바 -->
</Grid.RowDefinitions>
```

**변경 후:**
```xml
<Grid.RowDefinitions>
    <RowDefinition Height="Auto"/> <!-- 메뉴바 (신규) -->
    <RowDefinition Height="40"/>   <!-- 타이틀바 -->
    <RowDefinition Height="56"/>   <!-- 상태/도메인 바 -->
    <RowDefinition Height="*"/>    <!-- 채팅 영역 -->
    <RowDefinition Height="Auto"/> <!-- 입력 바 -->
</Grid.RowDefinitions>
```

### 3-2. 기존 4개 Row의 Grid.Row 값 +1

| 요소 | 라인 | 기존 | 변경 |
|------|------|------|------|
| 커스텀 타이틀바 `<Border>` | 105 | `Grid.Row="0"` | `Grid.Row="1"` |
| 상태/도메인 바 `<Border>` | 177 | `Grid.Row="1"` | `Grid.Row="2"` |
| 채팅 영역 `<Grid>` | 235 | `Grid.Row="2"` | `Grid.Row="3"` |
| 입력 바 `<Border>` | 288 | `Grid.Row="3"` | `Grid.Row="4"` |

### 3-3. Row 0 — Menu XAML 스니펫 (인라인 다크 스타일 포함, 복붙 가능)

`<Grid.RowDefinitions>` 닫는 태그 바로 다음(즉, 타이틀바 `<Border Grid.Row="1">` 위)에 다음 XAML을 그대로 삽입한다.

```xml
<!-- ── 메뉴바 (신규) ─────────────────────────────────────── -->
<Menu Grid.Row="0"
      Background="{StaticResource SurfaceBg}"
      Foreground="{StaticResource TextPrimary}"
      BorderBrush="{StaticResource BorderBrush}"
      BorderThickness="0,0,0,1"
      Padding="6,2"
      FontSize="12">
    <Menu.Resources>
        <!-- 최상위 MenuItem (헤더) -->
        <Style TargetType="MenuItem">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{StaticResource TextPrimary}"/>
            <Setter Property="Padding" Value="10,4"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Style.Triggers>
                <Trigger Property="IsHighlighted" Value="True">
                    <Setter Property="Background" Value="{StaticResource Surface2Bg}"/>
                    <Setter Property="Foreground" Value="White"/>
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Foreground" Value="{StaticResource TextMuted}"/>
                </Trigger>
            </Style.Triggers>
        </Style>

        <!-- 드롭다운 패널(Submenu) 다크 배경 -->
        <Style TargetType="Separator" x:Key="MenuSeparator">
            <Setter Property="Background" Value="{StaticResource BorderBrush}"/>
            <Setter Property="Height" Value="1"/>
            <Setter Property="Margin" Value="4,4"/>
        </Style>
    </Menu.Resources>

    <MenuItem Header="파일(_F)">
        <!-- 드롭다운 영역 자체의 배경: MenuItem.SubmenuOpened 시 시각 보정 -->
        <MenuItem Header="단축키 설정(_S)..."
                  Click="MenuItem_HotkeySettings_Click"
                  InputGestureText="Ctrl+,"/>
        <Separator Style="{StaticResource MenuSeparator}"/>
        <MenuItem Header="종료(_X)"
                  Click="MenuItem_Exit_Click"
                  InputGestureText="Alt+F4"/>
    </MenuItem>
</Menu>
```

> **참고:** WPF의 `Menu` 드롭다운(Popup) 배경은 시스템 테마를 따른다. 완전한 다크 드롭다운을 원하면 `ContextMenu`/`MenuItem` 의 `Template`을 재정의해야 하는데, 이는 분량이 커서 본 설계에서는 인라인 `Style` 트리거(IsHighlighted)로 헤더 호버만 다크 처리한다. 드롭다운 패널 배경까지 완전 다크 처리가 필요하면 5번 항목의 Styles.xaml 확장안을 사용한다.

---

## 4. MainWindow.xaml.cs 변경

### 4-1. using 별칭 보강

기존 파일 상단 `using` 섹션은 그대로 유지하되, ViewModel/View 네임스페이스가 추가로 필요하다.

```csharp
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using OhMyAgent.AiAgent.Client.ViewModels;
using OhMyAgent.AiAgent.Client.Views;          // ← 추가 (SettingsWindow)
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;
```

### 4-2. 핸들러 추가 위치

`OnSourceInitialized` 메서드 아래(클래스 끝)에 추가한다.

### 4-3. MenuItem_HotkeySettings_Click 핸들러

App의 트레이 메뉴 "Settings" 항목과 동일한 동작을 그대로 따른다(`App.xaml.cs` line 145~153 참고).

```csharp
    /// <summary>
    /// [파일 → 단축키 설정] 클릭 — SettingsWindow를 모달리스로 띄운다.
    /// 트레이 메뉴 "Settings"와 동일한 동작.
    /// </summary>
    private void MenuItem_HotkeySettings_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var settings = app.SettingsService;
        if (settings == null) return;   // App OnStartup 이전 호출 방어

        var settingsVm = new SettingsViewModel(settings);
        var settingsWindow = new SettingsWindow(settingsVm)
        {
            Owner = this   // 부모를 MainWindow로 두어 z-order 정렬
        };
        settingsWindow.Show();
        settingsWindow.Activate();
    }
```

### 4-4. MenuItem_Exit_Click 핸들러

요구사항대로 **종료 확인 다이얼로그 없이** 바로 `App.ExitApplication()`을 호출한다. `OnClosing`의 종료 다이얼로그를 우회하기 위해 `App.IsExiting` 플래그가 `ExitApplication()` 안에서 `true`로 설정되므로, 이후 발생하는 윈도우 Close에서 다이얼로그가 뜨지 않는다.

```csharp
    /// <summary>
    /// [파일 → 종료] 클릭 — 확인 다이얼로그 없이 즉시 앱을 종료한다.
    /// </summary>
    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).ExitApplication();
    }
```

---

## 5. Resources/Styles.xaml 변경 (선택)

위 3-3의 인라인 스타일로 헤더 호버 다크화는 충족된다. 단, **드롭다운 패널 자체의 배경/테두리**까지 완전한 다크 테마로 통일하고 싶다면 다음 스타일을 `Styles.xaml`에 추가하고, MainWindow의 `<Menu>` 와 `<MenuItem>` 에서 `Style="{StaticResource DarkMenu}"`, `ItemContainerStyle="{StaticResource DarkMenuItem}"` 형태로 적용한다.

본 설계의 기본 권고는 **인라인으로 처리하고 Styles.xaml은 변경하지 않음**이다. 추후 메뉴바가 확장되어 다른 화면에서도 재사용된다면 그때 분리한다.

---

## 6. 주의사항

### 6-1. Application 모호 참조 방지
- `App.xaml.cs`와 `MainWindow.xaml.cs` 모두 이미 `using Application = System.Windows.Application;` 별칭을 사용 중이다. 추가 핸들러에서 `Application.Current` 참조 시 동일 별칭이 그대로 작동한다.
- `using OhMyAgent.AiAgent.Client.Views;` 를 추가할 때 `SettingsWindow` 외 다른 충돌이 없는지(특히 `Views.Converters` 와의 충돌) 확인. 현재 `Converters.cs`는 클래스이므로 충돌 없음.

### 6-2. SettingsService null 처리
- `App.SettingsService` getter는 `null!` 단언이지만, 이론상 `OnStartup` 완료 전 호출되면 NRE가 난다. MainWindow 핸들러는 사용자 클릭으로만 호출되므로 사실상 안전하나, 방어적으로 핸들러 안에서 `if (settings == null) return;` 가드를 둔다(4-3에 반영됨).

### 6-3. 종료 흐름 정합성
- `MenuItem_Exit_Click` → `App.ExitApplication()` 호출 시 내부에서 `IsExiting = true` 설정 후 `Shutdown()`이 호출된다.
- WPF `Shutdown()`은 모든 Window의 `Closing`/`Closed`를 발생시키지만, `MainWindow.OnClosing`은 `IsExiting == true` 분기에서 즉시 `base.OnClosing(e)` 호출 후 리턴하므로 종료 확인 다이얼로그가 뜨지 않는다(line 56~60). 요구사항(다이얼로그 없이) 충족.

### 6-4. 메뉴바 위치와 드래그 영역
- 본 설계는 메뉴바를 Row 0(최상단)으로 두므로, 사용자가 **창 최상단을 드래그해도 더 이상 창 이동이 되지 않는다** (메뉴바는 마우스 이벤트를 가로챈다). 창 이동은 기존 타이틀바(Row 1) 영역에서만 가능.
- 만약 "메뉴바를 타이틀바 아래에 두는" 대안을 선호한다면:
  - RowDefinitions에서 신규 Auto Row를 **Row 1 위치**에 삽입
  - 타이틀바 Grid.Row=0 유지
  - 상태바/채팅/입력바만 +1
  - Menu의 `Grid.Row="1"` 로 지정

### 6-5. 액세스 키(Alt 단축키)
- `Header="파일(_F)"` 의 언더스코어 `_F` 는 WPF MenuItem의 액세스 키 표기. Alt 누르면 'F'에 밑줄이 표시된다.
- `InputGestureText="Ctrl+,"` 는 우측에 단축키 힌트 텍스트만 그리며, 실제 키 바인딩은 별도. 실제로 Ctrl+,로 설정 창을 열고 싶다면 `Window.InputBindings`에 `KeyBinding`을 추가해야 한다(본 요구사항 범위 외).

### 6-6. 테마 일관성
- `Menu.Background = SurfaceBg(#161B22)` 로 타이틀바와 동일한 색을 사용해 시각적 연속성 확보.
- 호버 색은 `Surface2Bg(#21262D)` — 입력 바 텍스트 박스와 같은 단계의 표면 색.
- 드롭다운 패널은 시스템 기본(라이트)일 수 있다는 점에 유의(5번 참고).

---

## 7. 구현 체크리스트 (구현 단계용)

- [ ] `App.xaml.cs`: `internal ISettingsService SettingsService => _settingsService!;` 추가
- [ ] `MainWindow.xaml`: RowDefinitions에 `<RowDefinition Height="Auto"/>` 최상단 삽입
- [ ] `MainWindow.xaml`: 기존 4개 요소의 `Grid.Row` 값을 0→1, 1→2, 2→3, 3→4 로 변경
- [ ] `MainWindow.xaml`: Row 0에 `<Menu>` 블록 삽입 (3-3 스니펫)
- [ ] `MainWindow.xaml.cs`: `using OhMyAgent.AiAgent.Client.Views;` 추가
- [ ] `MainWindow.xaml.cs`: `MenuItem_HotkeySettings_Click` 추가
- [ ] `MainWindow.xaml.cs`: `MenuItem_Exit_Click` 추가
- [ ] 빌드 후 메뉴 동작 확인: 파일 → 단축키 설정 → SettingsWindow 표시 / 파일 → 종료 → 다이얼로그 없이 즉시 종료
