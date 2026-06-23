# UI 리스킨 — Fluent 다크 + 바이올렛/인디고 (Visual-only)

## 디자인 토큰

### 색상 (Colors.xaml)
| 토큰 | 값 | 비고 |
|------|----|----|
| WindowBg | #0B0D14 | 최하위 서피스 |
| SurfaceBg | #13161F | 카드/바 |
| Surface2Bg | #1B1F2A | 입력/콤보 |
| Surface3Bg | #232838 | 호버/트랙 (신규) |
| AccentBrush | #7C5CFF | 베이스 바이올렛 |
| AccentHoverBrush | #8F73FF | hover |
| AccentPressedBrush | #6A47E6 | pressed |
| AccentSoftBrush | #6366F1 | 보조 인디고 (섹션 헤더) |
| AccentSubtleBrush | #1F2540 | 호버 배경/칩 |
| AccentGlowBrush | #807C5CFF | 포커스 글로우 |
| UserBubbleGradient | #8268FF→#6A53F0 | 사용자 버블 그라데이션 (신규) |
| AccentGradient | #8268FF→#7C5CFF | 버튼/강조 (신규) |
| TextPrimary/Secondary/Muted | #E6E8F0 / #9CA3B4 / #5B6273 | 톤 현대화 |
| ConnectedDot | #34D399 | 에메랄드 |
| ErrorDot | #FB7185 | 로즈 |
| WarningBrush | #FBBF24 | 앰버 |
| BorderBrush/Light | #262B38 / #363C4C | 부드럽게 |

### 반경 / 섀도 / 폰트 (Styles.xaml)
- 코너 반경: 버튼/입력 11~12, 카드 14, 칩 8, 토글스위치 12
- 섀도: CardShadow(Blur24/Depth4/Op0.45), SoftShadow(Blur14/Depth2/Op0.35), 버튼 hover 글로우(Blur16, accent)
- 폰트: AppFont = "Segoe UI Variable Text, Segoe UI", MonoFont = "Cascadia Code, Consolas"
- 줄간격(LineHeight) 22→23, 여백 전반 확대

## 변경 파일
1. **Resources/Colors.xaml** — 바이올렛/인디고 팔레트 전면 교체, 서피스 4계층화, 그라데이션 2종 추가, 상태색 현대화
2. **Resources/Styles.xaml** — Fluent 재작성: PrimaryButton(그라데이션+hover 글로우), OutlineButton, CaptionButton(신규), DarkTextBox(포커스 글로우), DarkPasswordBox(신규), ComboBox 풀 템플릿(팝업 섀도/항목 칩), FluentToggleSwitch(신규), CheckBox, Slider, ThinScrollBar(thumb 템플릿), FloatingCard(신규), Chip(신규)
3. **Resources/TranscriptTemplates.xaml** — 사용자 버블 그라데이션+섀도, 어시스턴트 카드(아바타 칩+섀도), 시스템 라인 보더, 툴콜 카드(반경14+섀도, 위험도/상태 칩, hover 헤더), 승인 카드(섀도14)
4. **MainWindow.xaml** — 타이틀바 로고 칩+CaptionButton, 닫기 hover 로즈, 투명도 슬라이더 Fluent, placeholder 톤
5. **Views/ChatOnlyWindow.xaml** — 동일 타이틀/입력 스타일 적용(컴팩트·Topmost·NoTaskbar·Esc-hide 동작 보존)
6. **Views/SettingsWindow.xaml** — 3개 FloatingCard 섹션(에이전트/서버/단축키)으로 재구성, 로고 칩 타이틀바, DarkPasswordBox 적용
7. **Views/Converters.cs** — 하드코딩 색상값만 바이올렛/현대 톤으로 교체(로직 무변경): BoolToStatusBrush, ToolRiskToBrush(Execute→violet), ToolCallStatusToBrush(Running→violet)

## 보존 사항
- 모든 x:Name / Binding Path / Command / 컨버터 키 / DataContext 무변경
- 신규 컨버터 추가 없음(요구 불필요). 다크 테마·트레이·핫키·플로팅창 동작 보존

## 빌드 결과
- **오류 0개**
- 경고: NU1510(System.Drawing.Common, 사전 존재) + CS8767(ToolRegistry.cs, 사전 존재) — UI 변경과 무관, XAML 오류 없음
