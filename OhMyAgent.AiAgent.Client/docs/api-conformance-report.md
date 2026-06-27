# API 정합성 리포트 (Client ↔ Server)

- **서버 스펙**: `OhMyAgent.AiAgent.Server/docs/API-SPEC.md`
- **클라 프로젝트**: `OhMyAgent.AiAgent.Client`
- **범위**: C# 에이전트 클라이언트 계약 + user 엔드포인트. 어드민 웹(members/llm-providers 관리, statistics, roles, `/admin/*`)은 범위 밖.
- **빌드**: 0 에러 / 0 경고 (아래 [빌드 결과](#빌드-결과)).
- **요약**: ✅적합 18 · 🔧수정함 4 · ⚠️결정필요 3

---

## 대조 표

| 항목 | 스펙 요구 | 현재 상태 | 판정 | 파일:라인 |
|---|---|---|---|---|
| **A① tool_calls.arguments 직렬화** | assistant `tool_calls[].arguments` = JSON **문자열** | `ToolCallJsonConverter.Write` 가 `arguments` 를 `GetRawText()` 로 문자열 인코딩 | ✅적합 | `Services/ToolCallJsonConverter.cs:51` |
| **A② tool 메시지 tool_call_id** | tool 메시지에 `tool_call_id` | `AgentMessage.ToolCallId` (`tool_call_id`), `ToolResultMsg(...)` 가 세팅 | ✅적합 | `Models/Agent/AgentMessage.cs:24,46` |
| **A③ metadata 키 = workspace_root** | `metadata{os, workspace_root}` | `RequestMetadata` 가 `workspace_root` 사용(`workspace` 아님) | ✅적합 | `Models/Agent/RequestMetadata.cs:8` |
| **A③' metadata.os 값** | `os` 문자열(예: windows) | `"windows"` 하드코딩 전송 | ✅적합 | `Services/AgentOrchestrator.cs:184` |
| **A④ 첨부 와이어 포맷** | `attachments[]={file_name,content_type,size_bytes,data_base64}` | 와이어로 **전혀 전송 안 함**(컴포저가 전송 전 Clear). `Attachment` 모델은 `file_path/file_name/size_bytes/content_type`(로컬 UI 메타), `data_base64` 없음 | ⚠️결정필요 | `Models/Attachment.cs`, `Services/FileAttachmentService.cs:47`, `ViewModels/AgentSessionViewModel.cs:428` |
| **A⑤ temperature(선택)** | 0~2, 생략 가능 | 미전송(생략) — 누락은 갭 아님 | ✅적합 | `Models/Agent/AgentRequest.cs` |
| **A max_tokens** | 선택, 0/생략=미지정 | 상수 `DefaultMaxTokens` 전송 | ✅적합 | `Services/AgentOrchestrator.cs:181` |
| **B SSE message_start** | `{role, model}` | `MessageStart(id, model)` 파싱(서버는 `role` 전송 → `id` 는 빈값, 무해; 소비측 무시) | ✅적합 | `Services/AgentApiClient.cs:492`, `AgentOrchestrator.cs:83` |
| **B content_delta** | `{delta}` | `ContentDelta(GetString(root,"delta"))` | ✅적합 | `Services/AgentApiClient.cs:498` |
| **B tool_call** | `{id,name,arguments(string)}` | 문자열 arguments → 객체 재파싱(`Dispatch`) | ✅적합 | `Services/AgentApiClient.cs:500-539` |
| **B message_stop stop_reason** | `end_turn\|tool_use\|max_tokens` | raw 문자열 보존, `tool_use` 비교로 루프 분기. end_turn/max_tokens 는 종료 처리 | ✅적합 | `Services/AgentOrchestrator.cs:105-107` |
| **B message_stop usage** | `{prompt_tokens,completion_tokens,total_tokens}` | `Usage` 레코드 필드 일치 | ✅적합 | `Models/Agent/Usage.cs` |
| **B event: error** | `{error:{code,message}}` | 중첩 우선 + 평면 폴백 파싱 | ✅적합 | `Services/AgentApiClient.cs:548-554` |
| **C GET /models** | `{models:[{id,name,provider_type,active}]}` | `ModelInfo` 4필드 일치, `{models:[...]}` 언랩 | ✅적합 | `Models/Agent/ModelInfo.cs`, `AgentApiClient.cs:179` |
| **D GET /users/me** | `{username,display_name,organization,email}` 중첩 | `UserProfile` 4필드 일치 | ✅적합 | `Models/UserProfile.cs` |
| **D GET /me/quota** | flat `{windows:[...]}` | `QuotaResponse`/`QuotaWindow` 전 필드 일치 | ✅적합 | `Models/Agent/QuotaInfo.cs` |
| **D GET /tools/policy** | `{mode,enabled,disabled}` | `ToolPolicy` 일치, graceful null | ✅적합 | `Models/Agent/ToolPolicy.cs` |
| **D POST /tools/authorize** | `{tool,arguments?}`→`{allowed,reason}` | `AuthorizeRequestDto`/`ToolAuthorization` 일치 | ✅적합 | `Services/AgentApiClient.cs:334,613` |
| **D GET /client/version** | `{latest,minimum_supported,download_url?,notice?,mandatory}` | `ClientVersionInfo` 일치 | ✅적합 | `Models/Agent/ClientVersionInfo.cs` |
| **D POST /auth/login** | `{username,password}`→`{token}` | DTO 일치, Public(인증 미부착) | ✅적합 | `Services/AgentApiClient.cs:194,606` |
| **D GET /health** | Public 200 `{status,...}` | `IsSuccessStatusCode` 체크 | ✅적합 | `Services/AgentApiClient.cs:116` |
| **E 에러 envelope(중첩)** | 클라 계약 = `{error:{code,message}}` | `ReadErrorAsync` 가 중첩 우선 + 평면 폴백 | ✅적합 | `Services/AgentApiClient.cs:567-593` |
| **F① DELETE /projects/{id}** | 프로젝트 삭제 | **없었음** → 추가(graceful) + `ProjectService.DeleteAsync` 연동 | 🔧수정함 | `IAgentApiClient.cs`, `AgentApiClient.cs:472`, `ProjectService.cs:130` |
| **F② DELETE /projects/{id}/conversations/{cid}** | 대화 삭제 | **없었음** → 메서드 추가(graceful) | 🔧수정함 | `IAgentApiClient.cs`, `AgentApiClient.cs:494` |
| **F③ 프로젝트 upsert 바디** | `{client_id,name}` | **`{remote_id,name}` 오전송** → `{client_id,name}` 로 수정(클라 GUID 전송) | 🔧수정함 | `Models/Agent/RemoteProject.cs:18`, `ProjectService.cs:187` |
| **F④ 대화 upsert 바디** | `{client_id,title,created_utc,updated_utc,messages[]}` | **`{id,title,messages}`** 만 전송 → `{client_id,title,created_utc,updated_utc,messages}` 로 수정 | 🔧수정함 | `Models/Agent/RemoteProject.cs:30`, `ProjectService.cs:204` |
| **F⑤ RemoteProject 응답 필드** | `{id,name,created_utc,updated_utc,conversation_count}` | `id,name,updated_utc` 만 → `client_id,created_utc,conversation_count` 추가(옵셔널) | 🔧수정함 | `Models/Agent/RemoteProject.cs:7` |
| **G GET /agent/suggestions** | `?workspace_root=` → `{suggestions:[]}` | 실호출 미구현(`StubSuggestionService` 항상 빈 목록). 서버도 stub:[] | ⚠️결정필요(경미) | `Services/StubSuggestionService.cs` |
| **세션 동기화 /agent/sessions** | GET/GET{id}/PUT{id}/DELETE{id} | 클라 미채택(로컬 영속 사용) | ⚠️결정필요 | — |
| **PUT /me/password** | `{old_password,new_password}` | 클라 미구현(비번변경 화면 없음) | ⚠️결정필요 | — |

---

## 수정한 갭 요약 (무엇을 왜)

1. **DELETE 엔드포인트 2종 추가 (F①, F②)**
   - `IAgentApiClient`/`AgentApiClient` 에 `DeleteRemoteProjectAsync`, `DeleteRemoteConversationAsync` 추가.
   - 스펙은 `DELETE /projects/{id}` 와 `DELETE /projects/{id}/conversations/{cid}` 를 정의하나 클라에 **메서드 자체가 없었음**.
   - graceful 설계: remote_id 없으면 no-op, 오프라인/404/미지원도 예외 없이 no-op(멱등 삭제).
   - `ProjectService.DeleteAsync` 가 로컬 삭제 전, 동기화된 프로젝트면 서버측도 삭제하도록 연동.

2. **프로젝트 upsert 바디 키 정정: `remote_id` → `client_id` (F③)**
   - 스펙 `POST /projects` 바디는 `{client_id,name}` 이며 서버가 `client_id↔id` 로 **멱등 매핑**한다. 기존 코드는 `remote_id`(서버 id) 를 보내 신규 프로젝트 생성 시 항상 빈 값이 나가고 재전송 멱등성이 깨졌다.
   - 안정 키인 로컬 `ProjectRecord.Id`(클라 GUID) 를 `client_id` 로 전송하도록 수정.

3. **대화 upsert 바디 필드 보강: `created_utc`/`updated_utc` 추가 + `id`→`client_id` (F④)**
   - 스펙 바디는 `{client_id,title,created_utc,updated_utc,messages[]}`. 기존엔 `{id,title,messages}` 만 전송 → 키명 불일치 + 타임스탬프 누락.
   - `ChatSessionRecord.CreatedUtc/UpdatedUtc` 를 실어 보내고 `client_id` 로 키명 정정.

4. **RemoteProject 응답 필드 보강 (F⑤)**
   - 응답 스키마 `{id,name,created_utc,updated_utc,conversation_count}` 중 누락된 `client_id`/`created_utc`/`conversation_count` 를 옵셔널(기본값) 필드로 추가. 기존 동작(역직렬화는 추가 필드 무시 가능)은 불변.

> 모든 수정은 신규 NuGet 없이 STJ 만으로 처리했고, graceful(실패=null/no-op) 패턴을 유지했다.

---

## ⚠️ 결정 필요 항목 (제품/UI — 미구현, 리포트만)

1. **첨부 전송 경로 (A④)** — `attachments[]={file_name,content_type,size_bytes,data_base64}`
   - 현재 첨부는 **UI 칩으로만** 관리되고 전송 직전 Clear 되어 와이어로 나가지 않는다. `FileAttachmentService.ReadAsBase64Async` 는 `NotImplementedException` stub.
   - **권장**: 전송 시 `AgentMessage.Attachments` 에 와이어 전용 DTO(`{file_name,content_type,size_bytes,data_base64}`)를 채워 첨부한다. 파일당 ≤10MiB, 허용 MIME 외 400 가드. 현 `Attachment.file_path` 는 로컬 전용이므로 와이어 DTO와 분리(파일 경로 유출 방지). 텍스트 계열만 인라인/그 외 메타 노트는 서버 스펙대로.
   - 와이어 전송이 실제 켜질 때 함께 진행해야 하는 UI/UX 결정이라 본 작업에서 모델을 임의 변경하지 않음.

2. **서버 세션 동기화 `/agent/sessions` 채택 여부**
   - 스펙은 GET/GET{id}/PUT{id}/DELETE{id} 제공(서버 불투명 JSON 보관). 클라는 현재 로컬 영속만 사용.
   - **권장**: 멀티 디바이스 동기화가 제품 요구라면 PUT upsert + 목록 머지 채택. 단순 단일 PC면 미채택 유지. 제품 결정 필요.

3. **`PUT /me/password` 셀프 비밀번호 변경 화면**
   - 스펙은 `{old_password,new_password}` 제공. 클라엔 화면/메서드 없음.
   - **권장**: 설정/계정 화면에 비번 변경 폼 추가(평면 envelope, 400/401 처리). UI 추가가 필요한 제품 결정.

4. **(경미) `GET /agent/suggestions` 실호출 (G)** — 서버도 stub `[]` 라 기능 영향 없음. `ISuggestionService` 구현체만 HTTP 호출로 교체하면 되며, UI 변경 불필요. 서버가 실제 제안을 채우기 시작할 때 교체 권장.

---

## 빌드 결과

```
빌드했습니다.
    경고 0개
    오류 0개
```
컴파일 오류 **0** (CS/MC/XDG 없음). MSB3027/3021(exe 잠금) 미발생.
