# 디자인 토큰 가이드 (OhMyAgent.AiAgent.Client)

## 디자인 토큰이란?

**디자인 토큰**은 색상·글자 크기·모서리 반경·간격 같은 디자인 값에 **이름을 붙여 한 곳에 모아둔 변수**입니다.
화면 곳곳에 `FontSize="13"`처럼 숫자를 직접 적는 대신 `FontSize="{StaticResource FontSizeBody}"`처럼 **이름**으로 참조합니다.

웹의 CSS 변수(`:root { --font-body: 13px }` + `var(--font-body)`)와 같은 개념입니다.

> **핵심 워크플로우 — "값 하나 바꾸면 전역 반영"**
> 본문 글자를 13 → 14로 키우고 싶으면, 화면 수십 곳을 찾아 고칠 필요 없이
> `Tokens.xaml`의 `FontSizeBody` 값 **하나만** 바꾸면 이 토큰을 참조하는 **모든 화면이 한 번에** 반영됩니다.

---

## 어디서 무엇을 바꾸나 (파일 위치)

| 파일 | 담는 값 | CSS 비유 |
|------|---------|----------|
| `Resources/Colors.xaml` | **색상** (배경/텍스트/액센트/상태색/테두리) | 색 변수 |
| `Resources/Tokens.xaml` | **타이포 크기 · 모서리 반경 · 간격 스케일** | font-size / border-radius / spacing 변수 |
| `Resources/Styles.xaml` | **폰트 패밀리 · 그림자 · 컴포넌트 스타일**(버튼/카드/입력창 등) | font-family / box-shadow / 컴포넌트 CSS 클래스 |

> 로드 순서(App.xaml): **Colors → Tokens → Styles**. 따라서 Styles는 Colors/Tokens 토큰을 참조할 수 있습니다.
> `Tokens.xaml`(정의 원본)과 `Colors.xaml`(색 정의)은 **참조 대상**이며, 값 변경은 이 두 파일에서 합니다.

---

## 색상 토큰 (`Resources/Colors.xaml`)

키 이름으로 참조: `Foreground="{StaticResource TextPrimary}"`, `Background="{StaticResource SurfaceBg}"`

### 배경 / 서피스 계층

| 토큰 키 | 값(hex) | 용도 | CSS 대응 |
|---------|---------|------|----------|
| `WindowBg`   | `#0B0D14` | 최하단 윈도우 배경 | `background-color` |
| `SurfaceBg`  | `#13161F` | 카드/패널 1단계 표면 | `background-color` |
| `Surface2Bg` | `#1B1F2A` | 표면 2단계(겹친 영역) | `background-color` |
| `Surface3Bg` | `#232838` | 표면 3단계(트랙/구분) | `background-color` |
| `InputBg`    | `#10131B` | 입력창 배경 | `background-color` |

### 메시지 버블 / 그라데이션

| 토큰 키 | 값 | 용도 | CSS 대응 |
|---------|----|------|----------|
| `UserBubble`         | `#7C5CFF` | 사용자 메시지 버블(단색) | `background-color` |
| `AgentBubble`        | `#1B1F2A` | 에이전트 메시지 버블 | `background-color` |
| `UserBubbleGradient` | `#8268FF → #6A53F0` (대각) | 사용자 버블 그라데이션 | `linear-gradient(135deg, …)` |
| `AccentGradient`     | `#8268FF → #7C5CFF` (세로) | 강조 버튼/표면 그라데이션 | `linear-gradient(180deg, …)` |

### 텍스트

| 토큰 키 | 값(hex) | 용도 | CSS 대응 |
|---------|---------|------|----------|
| `TextPrimary`   | `#E6E8F0` | 본문/주요 텍스트 | `color` |
| `TextSecondary` | `#9CA3B4` | 보조 텍스트 | `color` |
| `TextMuted`     | `#5B6273` | 흐린/비활성 텍스트 | `color` |

### 액센트 (violet/indigo)

| 토큰 키 | 값(hex) | 용도 | CSS 대응 |
|---------|---------|------|----------|
| `AccentBrush`        | `#7C5CFF` | 기본 액센트 | `color` / `background` |
| `AccentHoverBrush`   | `#8F73FF` | 호버 상태 | `:hover` |
| `AccentPressedBrush` | `#6A47E6` | 눌림 상태 | `:active` |
| `AccentSoftBrush`    | `#6366F1` | 부드러운 보조 액센트 | `color` |
| `AccentGlowBrush`    | `#807C5CFF` | 포커스 글로우(반투명) | `box-shadow` 글로우 |
| `AccentSubtleBrush`  | `#1F2540` | 아주 옅은 액센트 배경 | `background` |

### 상태색

| 토큰 키 | 값(hex) | 용도 | CSS 대응 |
|---------|---------|------|----------|
| `ConnectedDot`  | `#34D399` | 연결됨(녹색 점) | `color` |
| `ErrorDot`      | `#FB7185` | 오류(적색 점) | `color` |
| `WarningBrush`  | `#FBBF24` | 경고(주황) | `color` |

### 무결성 상태색

| 토큰 키 | 값(hex) | 용도 |
|---------|---------|------|
| `IntegrityOkBrush`         | `#34D399` | 정상(녹색) |
| `IntegrityModifiedBrush`   | `#FBBF24` | 변조(주황) |
| `IntegrityCorruptedBrush`  | `#FB7185` | 손상(적색) |
| `IntegrityMissingBrush`    | `#9CA3B4` | 누락(회색) |
| `IntegrityUnexpectedBrush` | `#60A5FA` | 추가(청색) |

### 테두리

| 토큰 키 | 값(hex) | 용도 | CSS 대응 |
|---------|---------|------|----------|
| `BorderBrush` | `#262B38` | 기본 테두리 | `border-color` |
| `BorderLight` | `#363C4C` | 밝은 테두리/구분선 | `border-color` |

### 색상 값 (Color, 트리거/애니메이션용)

`SolidColorBrush`가 아닌 순수 `Color`로, 애니메이션(ColorAnimation)·트리거에서 사용합니다.

| 토큰 키 | 값(hex) |
|---------|---------|
| `AccentColor`        | `#7C5CFF` |
| `AccentHoverColor`   | `#8F73FF` |
| `AccentPressedColor` | `#6A47E6` |
| `ConnectedColor`     | `#34D399` |
| `ErrorColor`         | `#FB7185` |

---

## 타이포 토큰 (`Resources/Tokens.xaml`)

값은 DIP(≈ CSS px). 참조: `FontSize="{StaticResource FontSizeBody}"`

| 토큰명 | 값 | 용도 | CSS (font-size) |
|--------|----|------|-----------------|
| `FontSizeMicro`       | 9  | 초소형 배지/라벨 | `9px` |
| `FontSizeCaption`     | 10 | 캡션/타임스탬프 | `10px` |
| `FontSizeXSmall`      | 11 | 작은 라벨/보조 | `11px` |
| `FontSizeSmall`       | 12 | 작은 본문/메타 | `12px` |
| `FontSizeBody`        | 13 | **기본 본문** | `13px` |
| `FontSizeBodyLarge`   | 14 | 큰 본문/입력창 | `14px` |
| `FontSizeSubtitle`    | 15 | 소제목/주요 버튼 | `15px` |
| `FontSizeTitle`       | 18 | 제목 | `18px` |
| `FontSizeHeading`     | 26 | 큰 제목 | `26px` |
| `FontSizeHeadingLarge`| 28 | 더 큰 제목 | `28px` |
| `FontSizeDisplay`     | 36 | 디스플레이(아이콘/숫자 강조) | `36px` |

> 참고: **16**은 토큰이 없어 인라인 값으로 유지(Styles.xaml 일부 입력창). 필요 시 토큰을 추가하세요.

---

## 모서리 토큰 (`Resources/Tokens.xaml`)

단일값 대칭 모서리. 참조: `CornerRadius="{StaticResource RadiusMd}"`

| 토큰명 | 값 | CSS (border-radius) |
|--------|----|---------------------|
| `RadiusTiny`  | 2  | `2px` |
| `RadiusXs`    | 4  | `4px` |
| `RadiusSm`    | 6  | `6px` |
| `RadiusMd`    | 8  | `8px` |
| `RadiusLg`    | 10 | `10px` |
| `RadiusPill`  | 11 | `11px` (토글/칩 완전 둥근) |
| `RadiusXl`    | 12 | `12px` |
| `Radius2Xl`   | 14 | `14px` |
| `Radius3Xl`   | 18 | `18px` |

> **비대칭 모서리**(예: `16,16,4,16`, `18,18,0,0`)는 컴포넌트 고유 형태라 **토큰화하지 않고 인라인 유지**합니다.
> 토큰 없는 단일값(7, 9, 16, 17)도 인라인 유지 — 필요하면 토큰을 추가하세요.

---

## 간격 스케일 (`Resources/Tokens.xaml`)

> 이번 작업에서 Margin/Padding은 **교체하지 않았습니다**(비균일 여백이 많아 위험). 아래 토큰은 **신규 작업 시 사용** 권장.

### Double 간격 토큰 (`Space*`)

| 토큰명 | 값 | CSS (spacing) |
|--------|----|---------------|
| `Space1`   | 4  | `4px` |
| `Space1_5` | 6  | `6px` |
| `Space2`   | 8  | `8px` |
| `Space2_5` | 10 | `10px` |
| `Space3`   | 12 | `12px` |
| `Space3_5` | 14 | `14px` |
| `Space4`   | 16 | `16px` |
| `Space4_5` | 18 | `18px` |
| `Space5`   | 22 | `22px` |
| `Space6`   | 24 | `24px` |

### Thickness 균일 여백 프리셋

| 토큰명 | 값 | 용도 |
|--------|----|------|
| `SpaceXsAll` | 4  | 사방 4 균일 여백 |
| `SpaceSmAll` | 8  | 사방 8 균일 여백 |
| `SpaceMdAll` | 12 | 사방 12 균일 여백 |
| `SpaceLgAll` | 16 | 사방 16 균일 여백 |

> 비균일 여백(예: `24,0,24,18`)은 컴포넌트 고유라 인라인 유지합니다.

---

## 폰트 패밀리 / 그림자 (`Resources/Styles.xaml`)

### 폰트 패밀리

| 토큰명 | 값 | 용도 | CSS (font-family) |
|--------|----|------|-------------------|
| `AppFont`  | `Segoe UI Variable Text, Segoe UI, sans-serif` | 전역 기본 글꼴 | `font-family` |
| `MonoFont` | `Cascadia Code, Cascadia Mono, Consolas, Courier New` | 코드/모노스페이스 | `font-family: monospace` |

참조: `FontFamily="{StaticResource AppFont}"`

### 그림자 (DropShadowEffect)

| 토큰명 | 값 | 용도 | CSS (box-shadow) |
|--------|----|------|------------------|
| `CardShadow` | Blur 24, Depth 4, 아래방향, `#000` 45% | 떠 있는 카드(강한 그림자) | `0 4px 24px rgba(0,0,0,.45)` |
| `SoftShadow` | Blur 14, Depth 2, 아래방향, `#000` 35% | 가벼운 떠오름(부드러운 그림자) | `0 2px 14px rgba(0,0,0,.35)` |

참조: `Effect="{StaticResource CardShadow}"`

---

## 사용법 예시 (XAML)

```xml
<!-- 글자 크기: 숫자 대신 토큰 -->
<TextBlock Text="안녕하세요"
           FontSize="{StaticResource FontSizeBody}"
           FontFamily="{StaticResource AppFont}"
           Foreground="{StaticResource TextPrimary}"/>

<!-- 모서리 + 배경 + 그림자 -->
<Border CornerRadius="{StaticResource RadiusMd}"
        Background="{StaticResource SurfaceBg}"
        Effect="{StaticResource SoftShadow}"/>

<!-- Setter 안에서도 동일 -->
<Setter Property="FontSize"     Value="{StaticResource FontSizeBodyLarge}"/>
<Setter Property="CornerRadius" Value="{StaticResource Radius2Xl}"/>
```

---

## CSS ↔ XAML 매핑 치트시트 (웹 디자이너 온보딩)

| CSS 개념 | XAML 대응 | 메모 |
|----------|-----------|------|
| `var(--token)` | `{StaticResource TokenKey}` | 토큰 참조 방식 |
| `:root { --x: … }` | `Colors.xaml` / `Tokens.xaml`의 키 정의 | 토큰 정의 위치 |
| `.class { … }` (CSS 클래스) | `<Style x:Key="…" TargetType="…">` | 재사용 스타일 |
| 전역 element 스타일 `button { }` | `<Style TargetType="Button">` (키 없음) | 암묵 적용 |
| `:hover` | `<Trigger Property="IsMouseOver" Value="True">` | 마우스 오버 |
| `:active` | `<Trigger Property="IsPressed" Value="True">` | 눌림 |
| `:focus` | `<Trigger Property="IsKeyboardFocused" Value="True">` | 포커스 |
| `:disabled` | `<Trigger Property="IsEnabled" Value="False">` | 비활성 |
| `font-size` | `FontSize` | 단위 없음(DIP≈px) |
| `font-family` | `FontFamily` | |
| `color` | `Foreground` | 텍스트 색 |
| `background` | `Background` | |
| `border-color` | `BorderBrush` | |
| `border-width` | `BorderThickness` | |
| `border-radius` | `CornerRadius` | 단일값 또는 `좌상,우상,우하,좌하` |
| `padding` | `Padding` | `좌,상,우,하` 또는 단일/쌍 |
| `margin` | `Margin` | 동일 표기 |
| `box-shadow` | `Effect="{StaticResource …Shadow}"` | DropShadowEffect |
| `linear-gradient()` | `LinearGradientBrush` | StartPoint/EndPoint로 방향 |
| `display:flex; flex-direction:row` | `<StackPanel Orientation="Horizontal">` | 1차원 배치 |
| `display:flex; flex-direction:column` | `<StackPanel Orientation="Vertical">` | |
| `display:grid` | `<Grid>` + `RowDefinitions`/`ColumnDefinitions` | 2차원 배치 |
| `position:absolute` | `<Canvas>` + `Canvas.Left/Top` | 좌표 배치 |
| `transition` / `@keyframes` | `Storyboard` / `*Animation` (트리거 내) | 애니메이션 |

---

## 정리: 값 하나로 전역 반영하기

1. **색을 바꾸려면** → `Resources/Colors.xaml`에서 해당 브러시의 `Color` 값만 수정.
2. **글자 크기·모서리·간격을 바꾸려면** → `Resources/Tokens.xaml`에서 해당 토큰 값만 수정.
3. **글꼴·그림자·컴포넌트 모양을 바꾸려면** → `Resources/Styles.xaml`에서 수정.

해당 토큰을 `{StaticResource …}`로 참조하는 모든 화면이 **자동으로 일괄 반영**됩니다.
