---
name: wpf-architect
description: >
  WPF + MVVM 아키텍처 설계 스킬. 기능 요청을 MVVM 레이어(Model/Service/ViewModel/View)로
  분해하고 인터페이스 계약을 정의한다. wpf-orchestrator 내 Architect 에이전트가 사용.
  신규 기능 설계, 레이어 분해, 인터페이스 정의, 파일 구조 계획 시 사용.
---

# WPF 아키텍처 설계 가이드

## 목표

기능 요청을 받아 ServiceEngineer·ViewModelEngineer·UIDesigner가 병렬로 작업할 수 있도록
명확한 인터페이스 계약과 파일 구조를 정의한다.

## 레이어 분해 순서

1. **도메인 개념 추출** — 기능에서 명사(엔티티)와 동사(행위)를 추출한다.
2. **Model 정의** — 엔티티를 불변 record 또는 POCO 클래스로 정의한다.
3. **Service 인터페이스 정의** — 행위를 `I{Name}Service` 인터페이스의 메서드로 정의한다.
4. **ViewModel 설계** — View가 필요로 하는 프로퍼티·커맨드를 열거한다.
5. **View 계획** — 각 화면/컨트롤의 책임 범위를 정의한다.

## 네임스페이스 규칙

```
OhMyAgent.AiAgent.Client.Models       → Model 클래스
OhMyAgent.AiAgent.Client.Services     → 서비스 인터페이스 + 구현체
OhMyAgent.AiAgent.Client.ViewModels   → ViewModel 클래스
OhMyAgent.AiAgent.Client.Views        → Window, Page, UserControl
OhMyAgent.AiAgent.Client.Controls     → 재사용 커스텀 컨트롤
OhMyAgent.AiAgent.Client.Converters   → IValueConverter 구현체
OhMyAgent.AiAgent.Client.Commands     → 공통 ICommand 구현체
```

## Model 설계 원칙

- 가능하면 `record` 타입으로 불변 설계
- 직렬화 필요 시 `[JsonPropertyName]` 어트리뷰트 사용
- 유효성 규칙은 Model에 두지 않고 ViewModel 또는 Service에 위임

```csharp
// 예시
public record AgentMessage(
    string Id,
    string Content,
    DateTimeOffset Timestamp,
    MessageRole Role);
```

## Service 인터페이스 설계 원칙

- 반환 타입은 `Task<T>` 또는 `IAsyncEnumerable<T>`
- CancellationToken을 마지막 파라미터로 포함
- 실패는 도메인 예외(`AgentException` 등)로 표현

```csharp
// 예시
public interface IChatService
{
    Task<AgentMessage> SendMessageAsync(string content, CancellationToken ct = default);
    IAsyncEnumerable<AgentMessage> StreamResponseAsync(string sessionId, CancellationToken ct = default);
}
```

## ViewModel 설계 원칙

- 프로퍼티: `ObservableProperty` 어트리뷰트 또는 직접 구현
- 커맨드: `RelayCommand` / `AsyncRelayCommand`
- 생성자: 서비스 인터페이스를 파라미터로 받음

## 산출물 형식

`_workspace/01_architect_spec.md` 에 다음 구조로 작성:

```markdown
## 기능 명세
[한 줄 요약]

## Models
| 클래스명 | 타입 | 주요 필드 |
|---------|------|---------|

## Service 인터페이스
| 인터페이스 | 메서드 시그니처 |
|----------|--------------|

## ViewModels
| 클래스명 | 프로퍼티 | 커맨드 |
|---------|---------|-------|

## Views
| 파일명 | 담당 영역 | DataContext |
|-------|---------|-----------|

## 생성 파일 전체 경로
- OhMyAgent.AiAgent.Client/Models/...
- OhMyAgent.AiAgent.Client/Services/...
- ...

## 구현 제외 범위
[이번에 다루지 않는 것]
```
