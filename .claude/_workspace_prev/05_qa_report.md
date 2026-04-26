# 05. QA Report

## 결과: PASS

최초 빌드에서 `MainWindow.xaml.cs`의 `MessageBox` 모호성 오류 1건이 발견되어 직접 수정 후 재빌드한 결과, 오류 0개로 모든 검증 항목이 통과되었다.

## 발견된 이슈

| 파일 | 이슈 | 심각도 | 수정 여부 |
|------|------|--------|-----------|
| `MainWindow.xaml.cs` | `MessageBox` 가 `System.Windows.Forms.MessageBox` 와 `System.Windows.MessageBox` 사이 모호 참조 (CS0104). XAML partial-class wpftmp 빌드 컨텍스트에서 `App.xaml.cs` 의 `using System.Windows.Forms;` 가 흘러들어와 충돌 | High (빌드 실패) | Fixed - `using MessageBox = System.Windows.MessageBox;` 별칭 추가 |

## 항목별 검증 결과

### A. App.xaml.cs - PASS
- [x] `ExitApplication()` 가시성이 `internal` (line 172)
- 부가 확인: `IsExiting` static property, `Shutdown()` 호출 흐름 정상

### B. MainWindow.xaml - PASS
- [x] 타이틀바 버튼 순서: — (line 132) → 🔽 (line 143) → ✕ (line 152)
- [x] 🔽 버튼 `Click="TrayButton_Click"` 연결 (line 144)
- [x] — 버튼 `ToolTip="최소화"` (line 142)
- [x] 🔽 버튼 `ToolTip="트레이로 숨기기"` (line 151)
- [x] ✕ 버튼 `ToolTip="종료"` (line 160)

### C. MainWindow.xaml.cs - PASS (수정 후)
- [x] `MinimizeButton_Click` → `WindowState = WindowState.Minimized` (line 43)
- [x] `TrayButton_Click` 메서드 존재, `((App)Application.Current).HideToTray(this)` (line 46-47)
- [x] `CloseButton_Click` → `Close()` 만 호출 (line 50-51)
- [x] `OnClosing` 첫 줄 `if (App.IsExiting) { base.OnClosing(e); return; }` 가드 (line 55-59)
- [x] `MessageBox.Show YesNo` 다이얼로그 (line 61-67)
- [x] Yes → `((App)Application.Current).ExitApplication()` (line 71)
- [x] No → `e.Cancel = true` (line 75)
- [x] `base.OnClosing(e)` 마지막에 호출 (line 78)
- [x] `MessageBox` = `System.Windows.MessageBox` (별칭 추가로 보장) - **수정됨**
- [x] `Application` 별칭 존재 (`using Application = System.Windows.Application;`) (line 5)

### D. ChatOnlyWindow.xaml.cs - PASS
- [x] `CloseButton_Click` → `Close()` (line 29-30)
- [x] `OnClosing`에서 `App.IsExiting` 가드 (line 53-57)
- [x] `MessageBox.Show YesNo` 다이얼로그 — `System.Windows.MessageBox` 풀네임 사용 (line 59-65)
- [x] Yes → `((App)System.Windows.Application.Current).ExitApplication()` (line 69)
- [x] No → `e.Cancel = true` (line 73)
- [x] `OnKeyDown(Esc) → Hide()` 유지 (line 41-49)
- [x] `Application` 풀네임 사용 (`System.Windows.Application.Current`) — 모호성 없음

### E. ChatWindowCoordinator.cs - PASS
- [x] `ToggleChatOnly()` 첫 줄 `var main = _getMainWindow();` 후 `main.IsVisible` 체크 (line 28-29)
- [x] `IsVisible == true` (즉, MainWindow 표시 중)이면 `return` (line 30)

### F. 논리적 오류 - PASS
- [x] **MessageBox 중복 표시 없음**: 타이틀바 ✕ → `CloseButton_Click` → `Close()` → `OnClosing` 단일 경로. CloseButton_Click 자체는 다이얼로그를 띄우지 않고 Close()만 호출하므로 중복 없음.
- [x] **`ExitApplication()` 재발화 가드**: Yes 선택 → `ExitApplication()` → `IsExiting=true` → `Shutdown()` → 모든 윈도우 OnClosing 재발화 시 가드(`if (App.IsExiting) { base.OnClosing(e); return; }`)로 통과, 다이얼로그 재표시 없음.
- [x] **ChatOnlyWindow Esc 동작**: `OnKeyDown`에서 `Hide()` 호출 후 `e.Handled = true` → `Close()` 경로를 거치지 않으므로 `OnClosing`/종료 확인 다이얼로그 미발화. Hide만 수행됨.
- 부가 검토: `MainWindow.OnClosing` Yes 분기에서 `ExitApplication()` 후에도 `base.OnClosing(e)`가 실행되지만, `e.Cancel = false` 상태로 정상 닫힘 진행 + `IsExiting=true`이므로 ChatOnlyWindow 측은 가드로 통과. 부작용 없음.

### G. 빌드 - PASS (오류 0개 확인)
- 최초 빌드: 오류 1개 (`MainWindow.xaml.cs(61,22): CS0104 'MessageBox' 모호 참조`)
- 수정: `MainWindow.xaml.cs` 상단에 `using MessageBox = System.Windows.MessageBox;` 추가
- 재빌드 결과: **오류 0개**, 경고 6개 (모두 NuGet 패키지 관련 사전 경고로 본 변경과 무관)
  - NU1510 (System.Drawing.Common pruning)
  - NU1904 (Microsoft.SemanticKernel.Core 1.14.1 보안 취약점) — 사전 존재 이슈

## 수정 파일 목록
- `/mnt/c/Users/dkdlw/RiderProjects/OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client/MainWindow.xaml.cs` — `using MessageBox = System.Windows.MessageBox;` 별칭 1줄 추가
