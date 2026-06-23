# 07 — Codex 데스크톱 스타일 레이아웃 재구성 (UIDesigner)

> 다크/바이올렛(#7C5CFF) 토큰 재사용, 레이아웃만 Codex 앱 스타일(사이드바 + 메인)로 변경.
> 로직/바인딩명/DataContext/VM·Service·Model 무변경. View(XAML) + 리소스 + 신규 시각 컨버터 2종만 수정.
> 빌드: **오류 0개** (경고 5: 기존 NU1510·CS8767, UI 무관).

## 변경 파일
1. **MainWindow.xaml** — 전면 재구성: 좌측 260px 사이드바 + 우측 메인 2분할.
2. **Views/ChatOnlyWindow.xaml** — 입력 바를 컴팩트 라운드 컴포저(원형 전송/중지 토글)로 교체. 사이드바 없음(컴팩트 유지). 타이틀/트랜스크립트/Esc-hide/Topmost/NoTaskbar 동작 보존.
3. **Resources/Styles.xaml** — 신규 스타일 추가: `SidebarBg` 브러시(#070910), `SidebarItemButton`, `SidebarSectionHeader`, `ComposerSendButton`(원형 34px), `ComposerIconButton`, `ComposerChipButton`, `SuggestionItemButton`. 기존 스타일 무변경.
4. **Views/Converters.cs** — `CountToVisibilityConverter`(>0 ⇒ Visible), `EmptyCountToVisibilityConverter`(0 ⇒ Visible) 추가. 순수 시각 변환, 로직 무관.
5. **Resources/Converters.xaml** — 위 2종 등록(`CountToVisibility`, `EmptyCountToVisibility`).
6. **MainWindow.xaml.cs** — 변경 없음. 기존 핸들러 재사용(`MenuItem_HotkeySettings_Click`을 사이드바 설정 버튼/워크스페이스 칩에 연결). `MenuItem_Exit_Click`은 메뉴 제거로 미참조되나 무해하게 잔존.

## 레이아웃 구성

### 좌측 사이드바 (Column 0, 260px, near-black #070910)
- 상단 네비: **새 채팅**(연필 글리프 → `ClearCommand`), 검색·플러그인·자동화·모바일(정적 플레이스홀더, `IsEnabled=False`).
- **프로젝트** 섹션: `WorkspaceRoot` 표시 항목 + "현재 작업 공간"(파란 점=AccentSoft) — 정적.
- **채팅** 섹션: 트랜스크립트 비었을 때 "채팅 없음"(회색), 있을 때 "현재 세션" 항목으로 토글(`Transcript.Count` 컨버터).
- 하단 고정: **설정**(톱니 글리프 → `MenuItem_HotkeySettings_Click`, 기존 설정창 핸들러).

### 우측 메인 (Column 1)
- **상단 바(44px, 드래그)**: 좌=상태(연결 점 `IsConnected`/`StatusText`/`LastUsageText`), 우=모델/계정 칩(정적) + 투명도 슬라이더(`WindowOpacity`) + 캡션버튼(최소화/트레이/종료 — 기존 핸들러).
- **중앙 컨텐츠**: `Transcript.Count`로 토글
  - 빈 트랜스크립트 → **환영 화면**: 헤딩 "{WorkspaceRoot}에서 무엇을 작업할까요?"(FallbackValue "작업 공간") + 제안 항목 3개(정적).
  - 항목 있음 → 기존 **트랜스크립트 ItemsControl + TranscriptSelector**(MaxWidth 760 중앙).
  - 오류 시 → 기존 오류 패널(`HasError`/`ErrorMessage`/`RetryConnectionCommand`).
- **하단(Row 2, MaxWidth 760)**:
  - 인라인 승인 카드(`PendingApproval` + `ApprovalCardTemplate`).
  - 진행 ProgressBar(`IsBusy`).
  - **대형 라운드 컴포저 카드**(CornerRadius 18 + CardShadow): 멀티라인 입력(`InputText`, placeholder "무엇이든 해보세요") + 툴바 행
    - 좌: "+"(첨부, 플레이스홀더) / **승인 요청 ⌄ = 권한모드 ComboBox**(`PermissionModes`/`CurrentPermissionMode`).
    - 우: 모델 칩(정적 "모델 자동") / 마이크(플레이스홀더) / **원형 전송 버튼(↑ 글리프, `SendCommand`) ↔ 중지(`StopCommand`) `IsBusy` 토글**.
  - **My project 칩**: `WorkspaceRoot` 표시, 클릭 시 설정창 열기(폴더 선택 연결).

## VM 기능 매핑
| 이미지 요소 | 우리 앱 매핑 | 바인딩/핸들러 |
|---|---|---|
| 새 채팅 | 트랜스크립트 초기화 | `ClearCommand` |
| 승인 요청 ⌄ | 권한 모드 선택 | `PermissionModes` / `CurrentPermissionMode` (TwoWay) |
| 원형 전송 ↔ 중지 | 전송/중지 토글 | `SendCommand` / `StopCommand` + `IsBusy` |
| 입력창 | 입력 텍스트 | `InputText` (TwoWay) |
| My project 칩 | 작업 공간 표시·변경 | `WorkspaceRoot` + 설정창(`MenuItem_HotkeySettings_Click`) |
| 설정 | 설정창 열기 | `MenuItem_HotkeySettings_Click` (기존) |
| 환영↔트랜스크립트 | 빈상태 전환 | `Transcript.Count` (신규 Count/EmptyCount 컨버터) |
| 인라인 승인 | 승인 카드 | `PendingApproval` |
| 상태/사용량 | 상단 바 | `IsConnected`/`StatusText`/`LastUsageText` |
| 창 투명도 | 슬라이더 | `WindowOpacity` |

## 플레이스홀더(백엔드 없음, 정적 UI · `IsEnabled=False` 또는 클릭 무동작)
- 사이드바: 검색, 플러그인, 자동화, 모바일.
- 프로젝트/채팅 목록 항목(현재 세션 표시 외).
- 컴포저 "+"(첨부), 마이크, 모델 칩("모델 자동" 정적 — MainWindow VM에 모델 멤버 없음. 모델 선택은 SettingsViewModel 소관이라 환영 화면에서는 정적 표시).
- 환영 화면 제안 항목 3개.
- 하단 알림/상태 배너 카드: 별도 동적 배너 VM 멤버가 없어 **생략**(상태는 상단 바, 오류는 오류 패널, 승인은 인라인 카드로 이미 처리). 발명 금지 원칙 준수.

## 보존
- 모든 x:Name / Binding Path / Command / 컨버터 키 / DataContext 무변경.
- 다크 테마 + 바이올렛 강조(전송 버튼·활성 상태·포커스에만 절제 사용, 전반 뉴트럴 다크).
- 트레이/핫키/플로팅창(ChatOnlyWindow) 동작 보존. ChatOnlyWindow는 사이드바 없이 새 컴포저 스타일만 반영.
- 아이콘은 Segoe Fluent Icons/Segoe MDL2 글리프(외부 의존 없음).

## 아이콘 글리프 참고
새채팅 E70F·검색 E721·플러그인/연결 EA86·자동화 E9F5·모바일 E8EA·폴더 E8B7·문서 E8BD·설정 E713·칩펼침 E70D·최소화 E921·트레이 E944·닫기 E8BB·첨부 E710·마이크 E720·전송↑ E74A·중지 E71A·오류 E783·제안 E9D5.

## 빌드 결과
- **오류 0개 / 경고 5개**(NU1510 System.Drawing.Common ×3, CS8767 ToolRegistry ×2 — 모두 기존·UI 무관).
