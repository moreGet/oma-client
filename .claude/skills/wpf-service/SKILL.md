---
name: wpf-service
description: >
  WPF 앱의 Model 및 Service 레이어 구현 스킬. C# record/class 모델 생성,
  서비스 인터페이스 구현, HTTP/WebSocket/Named Pipe 통신, 비동기 패턴 적용.
  wpf-orchestrator 내 ServiceEngineer 에이전트가 사용.
---

# WPF Service & Model 레이어 구현 가이드

## Model 구현 패턴

### 불변 record (권장)
```csharp
public record AgentMessage(
    string Id,
    string Content,
    DateTimeOffset Timestamp,
    MessageRole Role)
{
    public static AgentMessage Create(string content, MessageRole role) =>
        new(Guid.NewGuid().ToString(), content, DateTimeOffset.UtcNow, role);
}

public enum MessageRole { User, Assistant, System }
```

### 뮤터블 클래스 (컬렉션에 담기거나 바인딩 대상이 되는 경우)
```csharp
public class AgentSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public List<AgentMessage> Messages { get; } = [];
}
```

## Service 구현 패턴

### 기본 구조
```csharp
public sealed class ChatService(HttpClient httpClient) : IChatService
{
    public async Task<AgentMessage> SendMessageAsync(
        string content, CancellationToken ct = default)
    {
        // 구현
    }
}
```

### 스트리밍 응답 (SSE / IAsyncEnumerable)
```csharp
public async IAsyncEnumerable<string> StreamResponseAsync(
    string sessionId,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await foreach (var chunk in _client.StreamAsync(sessionId, ct))
        yield return chunk;
}
```

### 예외 처리 원칙
- HttpRequestException, SocketException 등 인프라 예외를 도메인 예외로 변환
- 도메인 예외 기반 클래스: `AgentException(string message, Exception? inner)`
- 재시도는 Polly 또는 직접 구현 (최대 3회, exponential backoff)

## DI 등록 패턴

```csharp
// App.xaml.cs 또는 별도 ServiceCollectionExtensions
services.AddHttpClient<IChatService, ChatService>(client =>
{
    client.BaseAddress = new Uri("http://localhost:5000");
    client.Timeout = TimeSpan.FromSeconds(30);
});
```

## 비동기 원칙

- 모든 I/O: `async Task<T>` 반환
- UI 스레드 접근 없음 — Dispatcher 호출 금지
- `ConfigureAwait(false)` 는 라이브러리 코드에서만, 앱 코드에서는 생략 가능
- `CancellationToken`은 항상 마지막 파라미터로, 기본값 `default`

## 산출물 요약 형식

`_workspace/02_service_summary.md`:
```markdown
## 구현 완료 파일
- Models/AgentMessage.cs — record, 불변
- Services/IChatService.cs — 인터페이스
- Services/ChatService.cs — HttpClient 기반 구현

## 공개 API 요약
### IChatService
- SendMessageAsync(string content, CancellationToken) → Task<AgentMessage>
- StreamResponseAsync(string sessionId, CancellationToken) → IAsyncEnumerable<string>

## 추가 구현 사항 (Architect 명세에 없었으나 추가)
[없으면 생략]
```
