# 사내 AI API 서버 연동 인터페이스 명세 (API Contract)

> 요구사항 #2, #3 대응 문서.
> WPF 에이전트 클라이언트 ↔ 사내 AI API 서버(개발 중) 간 **연동 계약**을 정의한다.
> 서버 백엔드가 상용 AI API든 Local LLM이든, 이 계약만 충족하면 클라이언트는 동일하게 동작한다.
>
> 본 명세는 OpenAI / Anthropic 양쪽 tool-calling 포맷에 모두 매핑 가능한 **중립 포맷**으로 설계했다.
> 서버 개발팀은 이 계약을 자신들의 백엔드(예: vLLM, Ollama, 상용 API)로 변환만 하면 된다.

---

## 0. 공통 사항

- **Base URL**: 설정에서 지정 (예: `http://ai-gw.corp.local:8080`)
- **인증**: 모든 요청 헤더에 `Authorization: Bearer <token>` 또는 `X-Api-Key: <key>` (사내 정책에 맞춰 결정)
- **Content-Type**: `application/json` (요청), `text/event-stream` (스트리밍 응답)
- **타임아웃/재시도**: 연결 5초, 응답 스트리밍은 무제한(취소 토큰으로 제어)

---

## 1. 엔드포인트 요약

| Method | Path | 용도 | 필수 |
|--------|------|------|------|
| `GET`  | `/api/v1/health` | 연결/헬스 체크 | ✅ |
| `GET`  | `/api/v1/models` | 사용 가능한 모델 목록 | 선택 |
| `POST` | `/api/v1/agent/chat` | **에이전트 대화/도구호출 (핵심)** | ✅ |

---

## 2. `GET /api/v1/health`

연결 상태 확인. (현재 클라이언트 `CheckConnectionAsync`가 이미 사용)

**응답 200**
```json
{ "status": "ok", "version": "1.0.0", "backend": "local-llm | ai-api" }
```

---

## 3. `GET /api/v1/models` (선택)

```json
{
  "models": [
    { "id": "corp-llm-32b", "display_name": "사내 LLM 32B", "supports_tools": true, "supports_vision": false }
  ]
}
```

---

## 4. `POST /api/v1/agent/chat` — 핵심 에이전트 엔드포인트

에이전트 루프의 매 반복마다 호출된다. **클라이언트가 전체 대화기록을 전송하는 stateless 방식**(권장).

### 4.1 요청 (Request Body)

```json
{
  "model": "corp-llm-32b",
  "stream": true,
  "max_tokens": 4096,
  "messages": [
    { "role": "system", "content": "You are a Windows automation agent..." },
    { "role": "user", "content": "현재 폴더의 .log 파일을 모두 zip으로 묶어줘" },
    {
      "role": "assistant",
      "content": "로그 파일을 찾겠습니다.",
      "tool_calls": [
        { "id": "call_1", "name": "glob", "arguments": { "pattern": "**/*.log" } }
      ]
    },
    {
      "role": "tool",
      "tool_call_id": "call_1",
      "content": "{\"files\":[\"a.log\",\"b.log\"]}"
    }
  ],
  "tools": [
    {
      "name": "run_command",
      "description": "Execute a PowerShell or CMD command in the workspace directory.",
      "parameters": {
        "type": "object",
        "properties": {
          "shell":   { "type": "string", "enum": ["powershell", "cmd"] },
          "command": { "type": "string" }
        },
        "required": ["shell", "command"]
      }
    }
  ],
  "metadata": {
    "os": "windows",
    "workspace": "C:\\work\\project",
    "client_version": "1.0.0"
  }
}
```

#### 메시지 역할(role)
| role | 의미 | 추가 필드 |
|------|------|----------|
| `system` | 시스템 프롬프트(에이전트 정체성/규칙) | - |
| `user` | 사용자 입력 | - |
| `assistant` | 모델 응답 (텍스트 + 도구 호출) | `tool_calls[]` |
| `tool` | 도구 실행 결과 (클라이언트가 채워 넣음) | `tool_call_id` |

#### `tool_calls[]` 항목
| 필드 | 타입 | 설명 |
|------|------|------|
| `id` | string | 호출 고유 ID (tool_result와 매칭) |
| `name` | string | 호출할 도구 이름 |
| `arguments` | object | 도구 입력 (JSON Schema 준수) |

#### `tools[]` 항목 (= 클라이언트가 보유한 도구 스키마)
| 필드 | 타입 | 설명 |
|------|------|------|
| `name` | string | 도구 이름 |
| `description` | string | 모델용 설명 |
| `parameters` | object | JSON Schema |

> 클라이언트의 `IToolRegistry`가 이 `tools` 배열을 자동 생성한다.

### 4.2 응답 — SSE 스트림 (`stream: true`)

`text/event-stream`. 각 이벤트는 `event:` + `data:` 라인.

```
event: message_start
data: {"id":"msg_abc","model":"corp-llm-32b"}

event: content_delta
data: {"text":"로그 "}

event: content_delta
data: {"text":"파일을 압축하겠습니다."}

event: tool_call
data: {"id":"call_2","name":"run_command","arguments":{"shell":"powershell","command":"Compress-Archive *.log out.zip"}}

event: message_stop
data: {"stop_reason":"tool_use","usage":{"input_tokens":1200,"output_tokens":80}}

```

#### 이벤트 종류
| event | data | 설명 |
|-------|------|------|
| `message_start` | `{id, model}` | 응답 시작 |
| `content_delta` | `{text}` | 텍스트 토큰 스트림 (UI 실시간 표시) |
| `tool_call` | `{id, name, arguments}` | 모델이 도구 실행을 요청 |
| `message_stop` | `{stop_reason, usage}` | 응답 종료 |
| `error` | `{code, message}` | 오류 |

#### `stop_reason`
| 값 | 클라이언트 동작 |
|----|----------------|
| `tool_use` | 받은 `tool_call`들을 실행 → 결과를 `tool` 메시지로 추가 → **재요청(루프 계속)** |
| `end_turn` | 최종 답변 완료 → 루프 종료 |
| `max_tokens` | 길이 초과 → 사용자에게 알림/이어쓰기 |
| `error` | 오류 처리 |

> `tool_call` 이벤트가 1개 이상 오면 `stop_reason`은 반드시 `tool_use`.

### 4.3 비스트리밍 응답 (`stream: false`, 선택 지원)

```json
{
  "id": "msg_abc",
  "stop_reason": "tool_use",
  "content": "로그 파일을 압축하겠습니다.",
  "tool_calls": [
    { "id": "call_2", "name": "run_command", "arguments": { "shell": "powershell", "command": "Compress-Archive *.log out.zip" } }
  ],
  "usage": { "input_tokens": 1200, "output_tokens": 80 }
}
```

### 4.4 도구 실행 결과 반환 형식 (클라이언트 → 서버, 다음 루프 요청 시)

클라이언트는 도구 실행 후 `messages`에 아래를 append하여 **같은 엔드포인트로 재요청**한다.

```json
{
  "role": "tool",
  "tool_call_id": "call_2",
  "content": "{\"exit_code\":0,\"stdout\":\"out.zip 생성 완료\",\"stderr\":\"\"}",
  "is_error": false
}
```

| 필드 | 설명 |
|------|------|
| `tool_call_id` | 어떤 호출에 대한 결과인지 |
| `content` | 결과 본문(문자열; 구조화 결과는 JSON 문자열) |
| `is_error` | 실행 실패 여부 (모델이 에러를 인지하고 재시도하도록) |

---

## 5. 오류 응답 (HTTP 4xx/5xx)

```json
{ "error": { "code": "unauthorized | rate_limited | backend_error | bad_request", "message": "사람이 읽을 메시지" } }
```

---

## 6. 클라이언트 측 C# 인터페이스 (요구사항 #3 — 코드 계약)

본 HTTP 계약에 대응하는 클라이언트 추상화.

```csharp
// 서버 통신 (ChatService 리팩토링 대상)
public interface IAgentApiClient
{
    IAsyncEnumerable<AgentStreamEvent> SendAsync(AgentRequest request, CancellationToken ct);
    Task<bool> CheckHealthAsync(CancellationToken ct);
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct);   // 선택
}

// 에이전트 루프 오케스트레이터
public interface IAgentOrchestrator
{
    IAsyncEnumerable<AgentEvent> RunAsync(string userGoal, AgentSession session, CancellationToken ct);
    // 내부: SendAsync → tool_call 수신 → IToolRegistry 실행 → tool 결과 append → 반복
}

// 도구 계약
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonSchema ParametersSchema { get; }
    ToolRisk Risk { get; }                       // ReadOnly | Write | Execute | Destructive
    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct);
}

public interface IToolRegistry
{
    IReadOnlyList<ITool> All { get; }
    IReadOnlyList<ToolSchema> ToSchemas();        // 서버 요청의 tools[] 생성
    bool TryGet(string name, out ITool tool);
}

// 권한/승인 게이트
public interface IPermissionService
{
    Task<PermissionDecision> RequestAsync(ToolCall call, ToolContext ctx, CancellationToken ct);
}

// 작업 디렉토리 샌드박스
public interface IWorkspaceContext
{
    string Root { get; }                          // UI에서 지정
    string ResolvePath(string relativeOrAbsolute);// 경로 탈출 차단
    bool IsInsideWorkspace(string path);
}

// DTO 골격
public sealed record AgentRequest(string Model, bool Stream, int MaxTokens,
    IReadOnlyList<AgentMessage> Messages, IReadOnlyList<ToolSchema> Tools, RequestMetadata Metadata);

public abstract record AgentStreamEvent;
public sealed record MessageStart(string Id, string Model)                  : AgentStreamEvent;
public sealed record ContentDelta(string Text)                             : AgentStreamEvent;
public sealed record ToolCallEvent(string Id, string Name, JsonElement Args): AgentStreamEvent;
public sealed record MessageStop(string StopReason, Usage Usage)           : AgentStreamEvent;
public sealed record ErrorEvent(string Code, string Message)               : AgentStreamEvent;
```

---

## 7. 서버 개발팀에 전달할 핵심 요청사항

1. `/api/v1/agent/chat`가 **요청에 담긴 `tools` 스키마를 백엔드 LLM의 function-calling으로 전달**하고, 모델의 도구 호출을 `tool_call` 이벤트로 반환할 것.
2. **멀티턴 `tool` 결과 메시지를 이해**하고 이어서 추론을 계속할 것. (에이전트 루프의 핵심)
3. SSE 스트리밍을 기본 지원하되, `stream:false` 폴백도 지원하면 좋음.
4. `stop_reason`을 정확히 구분(`tool_use` vs `end_turn`)할 것 — 클라이언트 루프 종료 판단의 근거.
5. 인증 방식 확정(API Key / 사내 토큰 / mTLS).
6. Vision 도구(`screenshot`)를 쓸 경우, 이미지 입력 포맷(base64/멀티파트) 합의 필요.

---

## 8. Phase D 확장 — 향후 서버 기능 (클라이언트 stub/예약 필드 선반영)

> 아래 3기능은 클라이언트가 인터페이스/DTO/예약 필드를 **미리** 정의했고, 서버 구현 시 그대로 연동된다.
> 현재 클라이언트 동작: 동작 힌트=빈 목록 stub, 첨부=로컬 관리만(전송 미연결), 채팅 히스토리=로컬 영속.

### 8.1 GET /api/v1/agent/suggestions — 동작 힌트 (요구 G)
- Query: `?workspace_root={path}` (선택)
- 200 Response:
  ```json
  { "suggestions": [ { "text": "현재 프로젝트의 버그를 찾아 수정해 보세요", "prompt": "이 워크스페이스의 버그를 점검해줘", "icon": "E9D5" } ] }
  ```
- 클라이언트: `ISuggestionService.GetSuggestionsAsync(workspaceRoot, ct)` → `IReadOnlyList<Suggestion>`. 엔드포인트 부재 시 빈 목록(현 stub `StubSuggestionService`).
- DTO: `Suggestion(text, prompt?, icon?)` — `Models/Suggestion.cs`.

### 8.2 첨부 전송 — POST /api/v1/agent/chat 확장 (요구 D 서버측)
- 요청 §4.1 `messages[].` 에 **`attachments[]` 필드 예약**(현재 클라이언트가 직렬화하나 서버 미소비):
  ```json
  { "role": "user", "content": "이 파일 분석해줘",
    "attachments": [ { "file_name": "report.pdf", "content_type": "application/pdf", "size_bytes": 10240, "data_base64": "..." } ] }
  ```
- 클라이언트 현재: `AgentMessage.Attachments`(file_path/file_name/size_bytes/content_type)만 보유, `data_base64`는 미전송(`IFileAttachmentService.ReadAsBase64Async` stub).
- `AgentMessage.Attachments`는 null이면 직렬화 생략(`AgentJson.Options` WhenWritingNull)이므로 기존 요청 바이트 불변 — 서버 회귀 없음.
- 서버 합의 필요: 최대 파일 크기, 허용 MIME, base64 inline vs 멀티파트 업로드 엔드포인트(`POST /api/v1/agent/attachments` 별도 권장).

### 8.3 채팅 히스토리 서버 동기화 (요구 C 서버측, 선택)
- 현재 로컬 `%APPDATA%/OhMyAgent/sessions/{id}.json` 단독. 미래 동기화용 엔드포인트 초안:
  - `GET /api/v1/agent/sessions` → `ChatSessionSummary[]`
  - `GET /api/v1/agent/sessions/{id}` → `ChatSessionRecord`
  - `PUT /api/v1/agent/sessions/{id}` (upsert) / `DELETE /api/v1/agent/sessions/{id}`
- DTO: `ChatSessionRecord(id, title, created_utc, updated_utc, workspace_root?, messages[])`, `ChatSessionSummary(id, title, updated_utc, workspace_root?, message_count)`.
- 클라이언트: `IChatHistoryService`가 추상화 경계 — 로컬 구현(`ChatHistoryService`)을 서버 구현으로 교체 가능. 현재 미연동.

### 8.4 서버팀 결정 요청
1. 동작 힌트 엔드포인트 제공 여부/스키마.
2. 첨부 전송 방식(inline base64 vs 별도 업로드) + 한도.
3. 채팅 히스토리 서버 보관 정책(클라 단독 유지 vs 동기화).

---

> 관련 문서: [AGENT_ARCHITECTURE_PLAN.md](./AGENT_ARCHITECTURE_PLAN.md) — 전체 전환 계획
