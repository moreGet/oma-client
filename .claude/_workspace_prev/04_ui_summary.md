# 04. UI Layer Summary

뷰 레이어 3개 파일에 대한 수정 요약. 설계 문서(`01_architect_spec.md`) 기준 정확 구현.

---

## 수정 파일 1: `OhMyAgent.AiAgent.Client/MainWindow.xaml`

### 변경 위치
타이틀바 우측 버튼 `StackPanel` 내부 (라인 132~171).

### 변경 내용
- 기존 최소화 버튼(`—`)에 `ToolTip="최소화"` 추가.
- 최소화 버튼과 닫기 버튼 사이에 신규 트레이 버튼(`🔽`) 삽입.
  - `Click="TrayButton_Click"`, `FontSize="13"`, `ToolTip="트레이로 숨기기"`.
  - 그 외 시각 속성(36x28, Transparent, BorderThickness=0, TextSecondary, Cursor=Hand)은 기존 패턴 유지.
- 기존 닫기 버튼(`✕`)에 `ToolTip="종료"` 추가. Hover 빨간색 스타일은 그대로 보존.

### 최종 시각 순서 (좌 → 우)
`[투명도 슬라이더] [— 최소화] [🔽 트레이] [✕ 종료]`

---

## 수정 파일 2: `OhMyAgent.AiAgent.Client/MainWindow.xaml.cs`

### 변경 1: `MinimizeButton_Click`
- 기존: `((App)Application.Current).HideToTray(this);` (트레이로 보냄)
- 변경: `WindowState = WindowState.Minimized;` (작업표시줄로 최소화)
- 주석: "최소화 → 작업표시줄로"

### 변경 2: `TrayButton_Click` (신규)
- 신규 핸들러 추가: `((App)Application.Current).HideToTray(this);`
- 위치: `CloseButton_Click` 바로 위에 배치.

### 변경 3: `CloseButton_Click`
- 기존: `HideToTray(this)` 호출
- 변경: `Close();` 만 호출. 다이얼로그 표시는 `OnClosing` 단일 진입점에서 처리.

### 변경 4: `OnClosing` 전체 교체
- `App.IsExiting == true`인 경우(트레이 메뉴 → Exit 등) 즉시 `base.OnClosing(e)` 후 return → 다이얼로그 미표시, 정상 종료.
- 그 외에는 `MessageBox.Show(this, "프로그램을 종료하시겠습니까?", "OhMyAgent", YesNo, Question, No)` 표시.
  - Yes → `((App)Application.Current).ExitApplication();` 호출 (앱 완전 종료).
  - No → `e.Cancel = true;` (창 유지).
- 마지막에 `base.OnClosing(e);` 호출.

### 변경 없음
- `OnSourceInitialized`, `InputBox_KeyDown`, `Messages_CollectionChanged`, `TitleBar_MouseLeftButtonDown` — 그대로 유지.

---

## 수정 파일 3: `OhMyAgent.AiAgent.Client/Views/ChatOnlyWindow.xaml.cs`

### 변경 1: `CloseButton_Click`
- 기존: `=> Hide();`
- 변경: `=> Close();`

### 변경 2: `OnClosing` 전체 교체
- `App.IsExiting`이면 즉시 `base.OnClosing(e)` 후 return.
- 그 외에는 `System.Windows.MessageBox.Show(this, "프로그램을 종료하시겠습니까?", "OhMyAgent", YesNo, Question, No)` 표시.
  - Yes → `((App)System.Windows.Application.Current).ExitApplication();` 호출.
  - No → `e.Cancel = true;`.
- 마지막에 `base.OnClosing(e);` 호출.
- `MessageBox`/`Application` 은 `System.Windows.*` 풀네임으로 호출하여 `System.Windows.Forms` 와의 충돌 방지(파일 상단에 별도 alias 없음).

### 변경 없음
- `OnKeyDown(Esc) → Hide()` — 그대로 유지. Esc는 "단순 숨김(임시 닫기)" 시맨틱 보존.
- `InputBox_KeyDown`, `Messages_CollectionChanged`, `TitleBar_MouseLeftButtonDown` — 그대로 유지.

---

## 시맨틱 변경 요약

| 액션 | 이전 동작 | 신규 동작 |
|------|----------|----------|
| MainWindow `—` 클릭 | 트레이로 숨김 | 작업표시줄로 최소화 |
| MainWindow `🔽` 클릭 | (없음) | 트레이로 숨김 (신규 전용 버튼) |
| MainWindow `✕` 클릭 | 트레이로 숨김 | 종료 확인 다이얼로그 → Yes면 완전 종료 |
| MainWindow Alt+F4 | 트레이로 숨김 | 종료 확인 다이얼로그 (OnClosing 단일 경로) |
| ChatOnly `✕` 클릭 | 숨김 | 종료 확인 다이얼로그 → Yes면 완전 종료 |
| ChatOnly Esc | 숨김 | 숨김 (변경 없음) |
| 트레이 메뉴 → Exit | 즉시 종료 | 즉시 종료 (App.IsExiting 가드) |

---

## 의존성

- `App.ExitApplication()` 가 `internal` 로 노출되어 있어야 호출 가능 (설계 문서 § App.xaml.cs 변경 참고).
- `App.IsExiting` 정적 프로퍼티 존재 가정.
- `App.HideToTray(Window)` 는 트레이 버튼 신규 핸들러에서만 호출.

## 검증 포인트

- MessageBox 중복 표시 방지: `CloseButton_Click → Close() → OnClosing` 단일 경로 사용.
- 트레이 메뉴 Exit 흐름: `IsExiting=true → Shutdown() → 모든 창 OnClosing 재발화 시 가드 통과` (다이얼로그 재표시 없음).
- 빌드 안전성: `using` 절 변경 없음. `MainWindow.xaml.cs` 는 `Application = System.Windows.Application` alias 활용. `ChatOnlyWindow.xaml.cs` 는 풀네임으로 충돌 회피.
