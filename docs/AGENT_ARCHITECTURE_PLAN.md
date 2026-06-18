# OhMyAgent — Windows 네이티브 에이전트 전환 계획

> 목표: 폐쇄망 사내 환경에서 Codex / Claude Code를 설치할 수 없으므로,
> **"사내 AI API 서버와 연동되어 Windows에서 사람이 할 수 있는 모든 작업을
> 바이너리 레벨로 수행하는 자율 에이전트(agentic loop)"** 를 WPF 네이티브 앱으로 구현한다.

---

## 1. 프로젝트 본질 재정의

| 구분 | 현재(As-Is) | 목표(To-Be) |
|------|-------------|-------------|
| 성격 | 채팅 클라이언트 + 제한적 액션 1개(`[ACTION:CREATE_FILE]`) | **자율 에이전트 실행 호스트 (Claude Code 류)** |
| AI 통신 | 단방향 텍스트 스트리밍 응답 | **Tool-calling 기반 멀티턴 에이전트 루프** |
| 액션 | 바탕화면에 고정 파일명 1개 생성 | **파일/셸/프로세스/UI 등 Windows 전 영역 도구화** |
| 실행 범위 | 없음(서버 응답 표시만) | **UI에서 지정한 작업 디렉토리(workspace) 안에서 자율 수행** |
| 안전장치 | 정규식 블랙리스트(스크립트 한정) | **권한 모드 + 승인 게이트 + 감사 로그 + 경로 샌드박스** |

핵심 전환점: **앱이 "응답을 받아 출력"하는 게 아니라, "서버가 내린 도구 호출을 실행하고 결과를 다시 서버로 돌려주는 루프"를 돈다.**

---

## 2. 아키텍처 개요

```
┌──────────────────────────────────────────────────────────────┐
│  WPF App (OhMyAgent.AiAgent.Client)                           │
│                                                              │
│  [UI Layer]  채팅/트랜스크립트 · 작업디렉토리 선택 · 승인 다이얼로그  │
│       │                                                      │
│  [ViewModel]  AgentSessionViewModel (루프 상태 머신)            │
│       │                                                      │
│  [Agent Core]                                                │
│     IAgentOrchestrator  ── 에이전트 루프 (질의→도구호출→실행→반환) │
│       │            │                                         │
│  IAgentApiClient   IToolRegistry ── ITool[] (도구 모음)         │
│       │            │      ├ RunCommand (PowerShell/CMD)       │
│       │            │      ├ ReadFile / WriteFile / EditFile   │
│       │            │      ├ ListDir / Glob / Grep             │
│       │            │      ├ Create/Move/Copy/Delete           │
│       │            │      └ (확장) Screenshot/UIAutomation/... │
│       │            │                                         │
│       │       IPermissionService ── 승인/권한 게이트            │
│       │       IWorkspaceContext  ── 작업 디렉토리 샌드박스        │
│       │       SecurityValidator  ── 위험 명령 차단(확장)         │
│       ▼                                                      │
└───────┼──────────────────────────────────────────────────────┘
        │ HTTP/SSE (API_CONTRACT.md 참조)
        ▼
┌──────────────────────────────────────────────────────────────┐
│  사내 AI API 서버 (개발 중)                                     │
│   - AI API(상용) 또는 Local LLM 백엔드를 추상화                  │
│   - Tool-calling 표준 프로토콜 제공                             │
└──────────────────────────────────────────────────────────────┘
```

---

## 3. 핵심: 에이전트 루프 (Agent Loop)

현재의 `StreamResponseAsync` 단방향 모델을 **반복 도구 호출 루프**로 교체한다.

```
1. 사용자가 작업(goal) 입력
2. 클라이언트 → 서버 : [대화기록 + 사용 가능한 도구 스키마] 전송
3. 서버 → 클라이언트 : 응답 (텍스트 토큰 스트림 + tool_call 0..N개)
4. stop_reason == "tool_use" 이면:
     a. 각 tool_call 을 권한 게이트(IPermissionService) 통과 검사
     b. IToolRegistry 에서 도구 찾아 작업 디렉토리 안에서 실행
     c. 실행 결과(stdout/파일내용/에러)를 tool_result 로 대화에 추가
     d. 2번으로 돌아가 다시 서버에 전송 (루프)
5. stop_reason == "end_turn" 이면 최종 답변 출력 후 루프 종료
6. 사용자가 언제든 Stop(CancellationToken) 가능
```

> 이것이 "클로드 코드 Agent처럼" 동작하는 본질이다. 도구를 몇 개 제공하느냐가 곧 에이전트의 능력 범위가 된다.

루프 안전장치:
- **최대 반복 횟수(max iterations)** 설정 — 무한 루프 방지
- **토큰/시간 예산** — 장시간 폭주 방지
- **사용자 Stop** — 즉시 취소
- 모든 도구 실행은 **감사 로그(audit log)** 에 기록

---

## 4. 도구 시스템 (Tool System)

### 4.1 도구 추상화
```csharp
public interface ITool
{
    string Name { get; }                 // "run_powershell"
    string Description { get; }           // 모델에게 줄 설명
    JsonSchema ParametersSchema { get; }  // 입력 파라미터 JSON Schema
    ToolRisk Risk { get; }               // ReadOnly | Write | Execute | Destructive
    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct);
}
```
- `IToolRegistry` 가 모든 도구를 보유하고, 서버로 보낼 **도구 스키마 목록**을 생성한다.
- `ToolContext` 는 현재 작업 디렉토리, 권한 모드 등을 담는다.

### 4.2 1차 도구 세트 (Claude Code 패리티 — MVP)

| 도구 | 설명 | 위험도 | 비고 |
|------|------|--------|------|
| `run_command` | PowerShell/CMD 명령 실행 (작업 디렉토리 기준) | Execute | **기존 ScriptExecutor 재사용** |
| `read_file` | 파일 읽기 (라인 범위 지원) | ReadOnly | |
| `write_file` | 파일 생성/덮어쓰기 | Write | `AgentActionService` 대체 |
| `edit_file` | 문자열 치환 기반 부분 수정 | Write | |
| `list_directory` | 디렉토리 목록 | ReadOnly | |
| `glob` | 패턴 기반 파일 검색 (`**/*.cs`) | ReadOnly | |
| `grep` | 파일 내용 검색 | ReadOnly | |
| `create_directory` | 디렉토리 생성 | Write | |
| `move` / `copy` / `delete` | 파일 이동/복사/삭제 | Destructive | 승인 필수 |

### 4.3 2차 도구 세트 (Windows 전용 확장 — "사람이 할 수 있는 모든 것")

| 도구 | 설명 | 비고 |
|------|------|------|
| `screenshot` | 화면/창 캡처 → 서버로 이미지 전달(비전 모델용) | |
| `ui_automation` | 마우스 클릭/키 입력/창 제어 (UIAutomation, SendInput) | GUI 자동화 |
| `process_control` | 프로세스 시작/종료/조회 | |
| `registry_read/write` | 레지스트리 접근 | Destructive |
| `http_fetch` | 사내망 HTTP 요청 | 폐쇄망 내부 자원 접근 |
| `clipboard` | 클립보드 읽기/쓰기 | |
| `env` | 환경변수 조회/설정 | |

> 2차 세트는 단계적으로 추가. "바이너리 레벨로 사람이 하는 모든 것"의 실체가 이 도구들의 확장이다.

---

## 5. 작업 디렉토리 & 샌드박스 (요구사항 #4)

- **UI에서 작업 디렉토리(workspace root)를 지정**한다. (`AppSettings.WorkspaceRoot`)
- `IWorkspaceContext` 가 모든 파일/셸 경로를 workspace root 기준으로 resolve.
- **경로 탈출(`..`, 절대경로) 방지** — 파일 도구는 기본적으로 workspace 밖 접근 차단(설정으로 완화 가능).
- 셸 명령도 기본 작업 디렉토리(`WorkingDirectory`)를 workspace로 설정.

---

## 6. 권한 / 승인 모델 (안전장치)

"사람이 할 수 있는 모든 것"을 풀어주는 만큼 안전 게이트가 필수.

### 6.1 권한 모드 (UI에서 선택)
| 모드 | 동작 |
|------|------|
| **Manual (기본 권장)** | 모든 Write/Execute/Destructive 도구 실행 전 사용자 승인 |
| **Auto-Safe** | ReadOnly 자동 / Write·Execute는 승인 / Destructive는 승인 |
| **Full-Auto (YOLO)** | 모든 도구 자동 실행 (블랙리스트만 차단) — 명시적 위험 고지 |

### 6.2 게이트 구성
- `IPermissionService.RequestAsync(toolCall)` → 승인/거부/항상허용
- **명령 화이트리스트/블랙리스트** — `SecurityValidator` 확장 (rm -rf, format, 레지스트리 파괴 등)
- **자동 승인 규칙** — 특정 도구/명령 패턴 자동 허용 목록(세션/영구)
- **감사 로그** — 모든 도구 호출/결과/승인 여부를 파일로 기록 (`%APPDATA%/OhMyAgent/audit/`)

---

## 7. UI 변경 사항

| 화면 | 변경 내용 |
|------|----------|
| 메인 윈도우 | **에이전트 트랜스크립트 뷰** — 도구 호출/파라미터/결과/상태(실행중·완료·실패)를 접이식으로 표시 |
| 입력 영역 | Stop 버튼, 권한 모드 선택, 진행 상태 표시 |
| 설정 | **작업 디렉토리 선택기**, 권한 모드 기본값, 최대 반복 횟수, 서버 URL/인증 |
| 승인 | 도구 실행 전 **인라인 승인 카드**(허용/거부/항상허용) |
| (옵션) | TODO/계획 패널 — 에이전트가 세운 단계 표시 |

기존 다크 테마/플로팅 창/트레이/핫키는 **그대로 유지**.

---

## 8. 기존 코드 정리 방침 (요구사항 #6)

### 유지 & 리팩토링
| 대상 | 처리 |
|------|------|
| WPF Shell / MVVM 인프라 / 다크테마 / 플로팅창 | **유지** |
| 트레이 / 전역 핫키 | **유지** (에이전트 즉시 호출에 그대로 유용) |
| `SettingsService` | **유지 + 확장** (WorkspaceRoot, PermissionMode, MaxIterations, Auth 추가) |
| `ScriptExecutor` + 동시성 제한 | **유지 → `run_command` 도구의 실행 엔진으로 재사용** |
| `SecurityValidator` | **유지 + 확장** (전 도구 대상 위험 차단) |
| `ChatService` | **리팩토링 → `AgentApiClient`** (tool-calling 프로토콜로 교체) |

### 교체 / 제거
| 대상 | 처리 | 사유 |
|------|------|------|
| `AgentActionService` + `[ACTION:CREATE_FILE]` 파싱 | **제거** | 정식 도구 시스템(`write_file`)으로 대체 |
| `Microsoft.SemanticKernel` 의존성 | **제거 검토** | 서버가 LLM 호출을 담당하므로 클라이언트엔 경량 도구 레지스트리가 더 적합 (결정 필요) |
| `McpSseServer` / `McpRemoteAgentService` (인바운드 MCP 서버 역할) | **제거 또는 보류** | 앱이 "도구를 제공하는 서버"가 아니라 "도구를 실행하는 에이전트"로 방향 전환. 단, 도구 실행 로직(ScriptExecutor 등)은 재사용. 향후 "다른 에이전트에 도구 노출"이 필요하면 부활 고려 |

---

## 9. 단계별 구현 로드맵

| 단계 | 내용 | 산출물 |
|------|------|--------|
| **Phase 0** | 계약 확정 · 코드 정리 · 의존성 결정 | 본 문서 + `API_CONTRACT.md` 합의, SK/MCP 처리 결정 |
| **Phase 1** | 에이전트 루프 + `AgentApiClient` + 1차 도구(파일/셸) + 작업 디렉토리 | "지정 폴더에서 파일 만들고 명령 실행하는 에이전트" 동작 |
| **Phase 2** | 권한 모드 + 승인 게이트 + 감사 로그 + 샌드박스 | 안전하게 자율 실행 |
| **Phase 3** | 트랜스크립트 UI + 세션 영속화 + Stop | 사용성/가시성 확보 |
| **Phase 4** | 2차 도구(스크린샷/UIAutomation/프로세스 등) | Windows 전 영역 자동화 |
| **Phase 5** | 폴리싱 · 에러 복원력 · 성능 · 배포 | 사내 배포 |

---

## 10. 결정 사항 (확정 / 미정)

| # | 항목 | 결정 |
|---|------|------|
| 1 | 서버 프로토콜 형태 | **stateless** (클라이언트가 전체 대화기록 보유, 복원력↑) — 권장안 채택 |
| 2 | Tool-calling 포맷 | **중립 포맷** (OpenAI/Anthropic 양쪽 매핑 가능) — `API_CONTRACT.md` 참조 |
| 3 | Semantic Kernel | **✅ 제거** — 경량 커스텀 도구 레지스트리로 대체 |
| 4 | MCP 인바운드 서버 | **✅ 제거** — 도구 실행 로직만 재사용, 서버 역할 폐기 |
| 5 | 권한 기본 모드 | **✅ Manual** — 모든 Write/Execute/Destructive 도구 실행 전 승인 |
| 6 | 인증 방식 | **미정** — 서버 개발팀과 협의 (API Key / 사내 토큰 / mTLS) |

> 다음 단계: **Phase 0(코드 정리) + Phase 1(에이전트 루프) 구현 착수**.

---

> 관련 문서: [API_CONTRACT.md](./API_CONTRACT.md) — 사내 AI API 서버 연동 인터페이스 명세
