# 04 — UI Summary (UIDesigner 산출물)

Views 레이어만 수정. 빌드 성공 (오류 0개, 기존 경고만 잔존: ToolRegistry CS8767 / BinaryIntegrityService CS8602 — 본 변경과 무관).

## 변경 파일

| 파일 | 변경 |
|------|------|
| `Views/SettingsWindow.xaml` | AuthScheme ComboBox 제거, 로그인(JWT) 그룹 추가, 인증 토큰 박스 "자동" 표시로 재배치 |
| `Views/SettingsWindow.xaml.cs` | `LoginPasswordBox_PasswordChanged` 핸들러 추가 |

> 실제 경로: `.../OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client/Views/`

---

## 제거된 컨트롤

서버 설정 카드(line 226-252 영역)의 2-컬럼 Grid:
- **AuthScheme ComboBox** — `ItemsSource="{Binding AuthSchemes}"` / `SelectedItem="{Binding AuthScheme}"` 완전 제거. (ViewModel에서 두 프로퍼티 삭제되어 남기면 바인딩 오류.)
- "인증 방식" 라벨 TextBlock 함께 제거.

## 추가된 컨트롤 (서버 설정 카드, 서버 URL 아래)

1. **로그인 입력 2-컬럼 Grid**
   - 사용자 ID `TextBox` → `Text="{Binding Username, UpdateSourceTrigger=PropertyChanged}"` (Style `DarkTextBox`)
   - 비밀번호 `PasswordBox x:Name="LoginPasswordBox"` + `PasswordChanged="LoginPasswordBox_PasswordChanged"` (Style `DarkPasswordBox`, 바인딩 없음)
2. **로그인 상태 + 버튼 Grid**
   - 상태 표시 `Ellipse` (점) + `TextBlock` → `Text="{Binding LoginStatus}"`.
     - `IsLoggedIn=True`이면 점/텍스트 색상이 `ConnectedDot`(녹색 #34D399)으로, 아니면 `TextMuted`/`TextSecondary`. DataTrigger 사용.
   - 로그인 `Button` → `Command="{Binding LoginCommand}"` (Style `OutlineButton`)
3. **인증 토큰(자동) 영역** — 기존 `AuthTokenBox` PasswordBox를 단독 행으로 재배치. 라벨을 "인증 토큰 (자동)"으로 변경. 로그인 성공 시 VM이 `AuthToken`을 채우며, 코드비하인드 생성자가 `AuthTokenBox.Password = vm.AuthToken`으로 시드(기존 동작 유지). 수동 입력 폴백도 가능(기존 `AuthTokenBox_PasswordChanged` 유지).

## PasswordChanged 핸들러 (코드비하인드)

```csharp
private void LoginPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
{
    if (DataContext is SettingsViewModel vm)
        vm.Password = LoginPasswordBox.Password;
}
```
- 기존 `AuthTokenBox_PasswordChanged` 패턴과 동일. `SettingsViewModel.Password`(비-ObservableProperty public 세터)에 직접 푸시.
- 네임스페이스: `OhMyAgent.AiAgent.Client.ViewModels.SettingsViewModel` (using 이미 존재).
- 로그인 성공 시 VM이 `Password=""`로 비우지만, PasswordBox.Clear() 동기화는 선택 사항이라 미구현(보안상 잔존 텍스트는 다음 입력 시 갱신).

## 주요 바인딩 매핑

| XAML 컨트롤 | Binding Path | 종류 | ViewModel 멤버 |
|-------------|-------------|------|----------------|
| TextBox (ID) | `Username` | TwoWay (PropertyChanged) | `Username` `[ObservableProperty]` |
| PasswordBox (LoginPasswordBox) | (바인딩 없음, 코드비하인드 푸시) | — | `Password` (public set) |
| Button (로그인) | `LoginCommand` | Command | `LoginCommand` (`LoginAsync`) |
| TextBlock (상태) | `LoginStatus` | OneWay | `LoginStatus` `[ObservableProperty]` |
| Ellipse / TextBlock (색상 트리거) | `IsLoggedIn` | OneWay (DataTrigger) | `IsLoggedIn` `[ObservableProperty]` |
| PasswordBox (AuthTokenBox) | (코드비하인드 시드/푸시) | — | `AuthToken` |

## 일관성 / 룩앤필

- 기존 다크 테마 리소스 키만 사용: `DarkTextBox`, `DarkPasswordBox`, `OutlineButton`, `TextSecondary`, `TextMuted`, `ConnectedDot`, `FloatingCard`. 인라인 하드코딩 색상 없음.
- 인증 토큰 위 2-컬럼 레이아웃은 기존 "실행 한도" 카드의 2-컬럼 Grid 컨벤션과 동일(`*`/12px gap/`*`).
- 서버 설정 카드 외 다른 카드/필드(ServerBaseUrl, ModelId, 모델 불러오기, MaxIterations 등) 무변경.

## 가정 / 메모

- 인증 토큰 PasswordBox는 제거하지 않고 "자동" 라벨로 유지(설계서 §Views 재량). 로그인 성공 시 자동 충전, 수동 입력 폴백도 동작.
- ViewModels/Models/Services 무수정.
