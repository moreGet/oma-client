---
name: wpf-qa
description: >
  WPF 코드 품질 검증 스킬. MVVM 패턴 준수, XAML 바인딩 정합성, 메모리 누수,
  비동기 패턴 오류, null 안전성 교차 검증. wpf-orchestrator 내 QAReviewer 에이전트가 사용.
  코드 리뷰, 바인딩 오류 검증, 패턴 위반 탐지 시 사용.
---

# WPF QA 검증 가이드

## 핵심 원칙: 경계면 교차 비교

"파일이 존재한다" 확인이 아니라, **ViewModel 프로퍼티 이름과 XAML Binding Path가 실제로 일치하는지** 두 파일을 동시에 읽어서 비교한다.

## 검증 절차

### Step 1: 바인딩 정합성 검사

`_workspace/03_viewmodel_summary.md`의 프로퍼티·커맨드 목록과 각 XAML 파일의 `{Binding XXX}` 경로를 대조한다.

```
ViewModel: InputText (string), SendCommand (ICommand)
XAML: Binding InputText ✅, Binding SendComand ❌ (오타)
```

불일치 발견 시 XAML 파일을 직접 수정한다.

### Step 2: MVVM 위반 탐지

**코드비하인드에서 금지 패턴 검색:**
```
MessageBox.Show          → DialogService로 교체
new {ServiceName}()      → 생성자 주입으로 교체
Application.Current.     → Dispatcher 직접 접근 여부 확인
```

**ViewModel에서 금지 패턴 검색:**
```
UIElement, Window, Button, TextBox  → View 레이어 참조 금지
Dispatcher.Invoke                    → SynchronizationContext 사용 권장
```

### Step 3: 메모리 누수 탐지

이벤트 구독이 있는 ViewModel에서 IDisposable 구현 또는 구독 해제 로직 확인:
```csharp
// 위험: 해제 없는 구독
someService.DataReceived += OnDataReceived;

// 안전: 해제 경로 있음
someService.DataReceived += OnDataReceived;
// + Dispose()에서 someService.DataReceived -= OnDataReceived;
```

### Step 4: 비동기 패턴 검증

```
async void (이벤트 핸들러 제외)  → async Task로 교체
.Result / .Wait()               → await로 교체
Thread.Sleep()                  → await Task.Delay()로 교체
```

### Step 5: Null 안전성

```csharp
// 위험
var name = message.Content.ToUpper();  // Content가 null일 수 있음

// 안전
var name = message.Content?.ToUpper() ?? string.Empty;
```

## 보고서 형식

`_workspace/05_qa_report.md`:
```markdown
## 검증 결과: PASS / FAIL

### 직접 수정한 항목
| 파일 | 수정 내용 |
|------|---------|
| Views/ChatWindow.xaml | Binding SendComand → SendCommand (오타 수정) |

### 발견된 문제 (미수정, 사용자 판단 필요)
| 파일 | 라인 | 문제 유형 | 설명 | 수정 방안 |
|------|------|---------|------|---------|

### 권고 사항 (블로커 아님)
- ChatViewModel에 IDisposable 구현 권고 (이벤트 구독 해제)
```

## 심각도 기준

| 심각도 | 기준 | 처리 |
|-------|------|------|
| **Critical** | 런타임 크래시 유발 (바인딩 오타, null ref) | 직접 수정 |
| **High** | MVVM 위반, 메모리 누수 가능성 | 보고 + 수정 방안 제시 |
| **Medium** | 비동기 패턴 비권장 | 보고 |
| **Low** | 스타일, 네이밍 컨벤션 | 권고 사항으로 기록 |
