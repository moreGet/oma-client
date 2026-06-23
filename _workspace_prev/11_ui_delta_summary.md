# 11 — UI 델타 구현 요약 (UIDesigner, Phase D)

스펙 `_workspace/08_delta_spec.md` §5 + ViewModel 멤버(`_workspace/10_viewmodel_delta_summary.md`)에 따라 Codex 스타일 레이아웃의 정적 플레이스홀더를 실제 VM 멤버에 바인딩. **빌드 0 에러** (잔존 경고 5건 전부 사전 존재: NU1510×3, CS8767×2 — 본 작업 무관). Fluent 다크+바이올렛 스타일/레이아웃 보존. VM/Service/Model 로직 무변경.

## 변경 파일
- `OhMyAgent.AiAgent.Client/MainWindow.xaml` — 사이드바 네비 정리 / 프로젝트·채팅 동적 목록 / 첨부 칩+버튼 / 마이크 삭제 / 인사 멘트 / 제안 동적.
- `OhMyAgent.AiAgent.Client/MainWindow.xaml.cs` — `AttachButton_Click` 핸들러 추가(OpenFileDialog Multiselect → `AddAttachmentPublic`).
- `OhMyAgent.AiAgent.Client/Views/SettingsWindow.xaml` — "사용자 프로필" 카드(UserDisplayName 입력 + SaveUserProfileCommand 버튼) 추가.

## 변경 미적용 (스펙대로 보존)
- `Views/ChatOnlyWindow.xaml` — 사이드바·마이크·제안 없음 → 무변경(마이크 부재 확인). 트레이/핫키/캡션 동작 보존.

## 요구사항별 변경 + 바인딩 매핑

| # | 요구 | 변경 | 바인딩 |
|---|------|------|--------|
| 1 | 네비 4개 삭제 | 검색/플러그인/자동화/모바일 Button 제거. "새 채팅"/"설정" 유지 | "새 채팅"=`ClearCommand`(AsyncRelayCommand·바인딩명 동일, 무변경) / "설정"=`MenuItem_HotkeySettings_Click` |
| 2 | 프로젝트=워크스페이스 히스토리 | 정적→`ItemsControl ItemsSource={Binding RecentWorkspaces}`. 폴더 글리프 E8B7 + DisplayName + 제거 X(E8BB). 빈 목록 "최근 작업 공간 없음" | 클릭=`OpenWorkspaceCommand` `{Binding}` / 제거=`RemoveWorkspaceCommand` `{Binding}` / 항목 `DisplayName`,`Path` / 빈상태=`RecentWorkspaces.Count`+`EmptyCountToVisibility` |
| 3 | 채팅=히스토리 | 정적→`ItemsControl ItemsSource={Binding ChatSessions}`. 문서 글리프 E8BD + Title + 삭제(E74D). 비었을 때만 "채팅 없음" | 클릭=`LoadChatSessionCommand` `{Binding}` / 삭제=`DeleteChatSessionCommand` `{Binding}` / 항목 `Title` / 빈상태=`ChatSessions.Count`+`EmptyCountToVisibility` |
| 4 | 컴포저 "+"=첨부 | "+" 버튼 활성화(`Click=AttachButton_Click`, `IsEnabled={Binding IsBusy,InverseBool}`). 입력 위 첨부 칩 `ItemsControl`(WrapPanel) 추가 | 칩=`Attachments`(FileName) / 제거=`RemoveAttachmentCommand` `{Binding}` / 표시=`HasAttachments`+`BoolToVisibility` / code-behind=`AddAttachmentPublic(path)` |
| 5 | 마이크 삭제 | 컴포저 툴바 마이크 Button(E720) + 해당 ColumnDefinition 제거. 전송/중지 토글 Column 5→4 재정렬 | — |
| 6 | 인사 멘트 | `<Run WorkspaceRoot>...무엇을 작업할까요?` → 단일 `Text="{Binding GreetingText}"` | `GreetingText` |
| 7 | 제안 동적 | 하드코딩 3개 제거 → `ItemsControl ItemsSource={Binding Suggestions}`, 비면 숨김(`CountToVisibility`). 항목 `Text` 표시(현재 빈 목록이라 미렌더) | `Suggestions`(`.Text`) / 가시성=`Suggestions.Count`+`CountToVisibility` |
| 8 | 설정창 프로필 | SettingsWindow 최상단 "사용자 프로필" 카드: 이름 TextBox + "프로필 저장" 버튼 | `UserDisplayName`(TwoWay) / `SaveUserProfileCommand` |

## 구현 노트
- ItemsControl 내부 부모 VM 커맨드: `Command="{Binding DataContext.{Cmd}, RelativeSource={RelativeSource AncestorType=ItemsControl}}" CommandParameter="{Binding}"` 패턴 사용(스펙 §5 권장 그대로).
- 첨부 칩 제거/목록 삭제 버튼은 기존 `ComposerIconButton` 스타일 축소(Width/Height/FontSize 인라인)로 재사용 — 신규 스타일 불요.
- 컴포저 카드 RowDefinitions 2→3행(첨부 칩 / 입력 / 툴바). 입력 Grid `Grid.Row` 0→1, 툴바 Grid `Grid.Row` 1→2로 시프트.
- 모든 컨버터는 기존 리소스(`CountToVisibility`/`EmptyCountToVisibility`/`InverseBool`/`BoolToVisibility`) 재사용 — 신규 컨버터 0건.
- 첨부 다이얼로그는 View 코드비하인드 소유(MVVM 안전): `Microsoft.Win32.OpenFileDialog{Multiselect=true}` → 선택 경로마다 `(DataContext as AgentSessionViewModel).AddAttachmentPublic(path)`.

## 빌드 결과
`dotnet build` → **오류 0개, 경고 5개**(NU1510 NuGet ×3, CS8767 ToolRegistry ×2 — 전부 사전 존재·본 델타 무관).
