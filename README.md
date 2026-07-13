# OhMyAgent.AiAgent.Client

폐쇄망 사내 환경을 위한 **Windows 네이티브 AI 에이전트 데스크톱 앱**입니다.
Codex / Claude Code를 설치할 수 없는 보안망에서, 사내 AI API 서버와 연동하여
**지정한 작업 디렉토리 안에서 파일·셸·시스템 작업을 자율적으로 수행**합니다.

> 단순 채팅 클라이언트가 아니라, 서버가 내린 **도구 호출(tool call)을 클라이언트가 실제로 실행하고
> 그 결과를 다시 서버로 돌려주는 에이전트 루프(agentic loop)** 를 도는 실행 호스트입니다.

---

## 핵심 특징

- **에이전트 루프**: `질의 → 도구 호출 → 실행 → 결과 반환 → 반복`을 `end_turn` 까지 자동 수행
- **27개 내장 도구**: 파일/셸부터 클립보드·프로세스·HTTP·스크린샷, 사무직 문서·데이터(CSV·Excel·PDF·Word), 작업 계획 추적(manage_todos)까지 (아래 표 참조)
- **멀티루트 워크스페이스**: 최대 **10개** 작업 디렉토리를 동시 등록, **폴더별 접근 허용/차단 토글**. 모든 파일/셸 작업이 활성 루트 기준으로 resolve되고 경로 탈출은 차단
- **프로젝트(대화 묶음)**: 여러 대화 세션을 상위 컨테이너로 묶어 관리. **로컬 우선 저장 + 선택적 서버 동기화**, 사이드바에서 대화를 **드래그앤드롭**으로 프로젝트에 분류
- **권한 게이트**: Manual / Auto-Safe / Full-Auto 3단계. 위험 작업은 실행 전 사용자 승인
- **시작 로그인 게이트**: 앱 시작 시 전용 로그인 화면(`LoginWindow`)에서 인증. 설정창에는 로그인 입력이 없고 로그아웃·상태만 표시
- **서버 프로필 표시**: 로그인 사용자의 이름/조직/이메일을 서버에서 조회해 설정창에 **읽기 전용**으로 노출(`GET /api/v1/users/me`)
- **토큰 쿼터**: 일/주/월 사용량·잔여를 상단바 칩 + 팝업 게이지로 표시(`GET /api/v1/me/quota`), **새로고침 버튼** 제공. 한도 초과(429)는 안내만 하고 로그아웃하지 않음
- **파일 첨부**: 컴포저에 첨부한 파일을 base64로 인코딩해 메시지에 실어 전송(파일당 ≤10MiB, `messages[].attachments[]`)
- **서버 세션 동기화**(선택): 대화 세션을 서버에 업서트/병합해 **여러 PC에서 공유**(`/api/v1/agent/sessions`, `updated_at` 최신 우선)
- **실시간 채팅(메신저)**: LLM 채팅과 **별개**인 사람↔사람 메신저. 단체/1:1 방, 송수신·수정·삭제, 읽음·안읽음 배지, 타이핑, 온라인 상태(presence), 멘션, 첨부, 멤버 관리를 **WebSocket**(`/api/v1/chat/ws`, 끊기면 지수 backoff 자동 재연결+이력 재동기화)+REST(`/api/v1/chat/*`)로 실시간 반영. 멤버는 **이름으로 표시**(UUID→이름, `GET /chat/rooms/{id}/members?detail=1` — 방 멤버 누구나). 트레이/사이드바에서 별도 메신저 창으로 진입 ([상세](docs/realtime-chat.md))
- **메신저 네트워크/UI 최적화**: 안읽음을 **로컬 카운터**로 추적해 메시지마다 `/chat/unread`를 호출하지 않음(서버 조회는 시작·재연결 시 1회). 읽음(read) POST는 **디바운스**, 방 열 때 멤버/presence는 **1회 공유 조회**, 1:1 상대·이름은 **캐시**. UI는 방 목록 **증분 갱신**(Clear 재생성 제거)으로 깜빡임 없음
- **서버 제어형 도구/보안**(선택, 2중 안전): 사용 가능 도구를 서버가 통제(`GET /api/v1/tools/policy`, cached/realtime) — 비활성 도구는 모델에 **노출조차 안 됨**. 위험 명령 차단 패턴도 **클라 디폴트 ∪ 서버 추가**(`GET /api/v1/security/command-policy`)로 운영하며, 서버 값이 없으면 클라 내장 디폴트만 적용
- **버전 관리 / 업데이트 알림**: SemVer + 빌드 git 해시. 서버 버전 점검으로 새/필수 버전 배너 안내(`GET /api/v1/client/version`)
- **사용자 친화 에러**: 서버 원문(영문/기술 문구) 대신 상태 코드 기준 한국어 안내로 변환. **401에서만 재로그인**, 403/429/404/5xx는 메시지만(로그아웃 없음)
- **로컬 우선**: 채팅 히스토리·프로젝트·설정은 로컬 영속(`%APPDATA%/OhMyAgent`), 서버는 stateless
- **데스크톱 통합**: 다크 테마, 시스템 트레이 상주, 전역 핫키(기본 `Ctrl+Space`), 플로팅 채팅창
- **배포 무결성**: 설치 바이너리 SHA-256 / Authenticode / HMAC 매니페스트 검증 (트레이 → 무결성 검사)

---

## 아키텍처

```
┌─ WPF App (OhMyAgent.AiAgent.Client) ─────────────────────────────┐
│  Views (XAML)        채팅·트랜스크립트 / 설정 / 무결성 / 승인 카드   │
│  ViewModels          AgentSessionViewModel(루프 상태) · Settings…  │
│  Agent Core                                                       │
│    IAgentOrchestrator   에이전트 루프 (질의→도구→실행→반환)         │
│    IAgentApiClient      서버 HTTP/SSE 통신(로그인·모델·chat·프로필·동기화)│
│    IToolRegistry        ITool[] 도구 모음 + 서버용 스키마 생성       │
│    IPermissionService   승인/권한 게이트(로컬)                     │
│    IToolPolicyService   서버 도구 정책 게이트(cached/realtime)      │
│    IWorkspaceContext    멀티루트 샌드박스(활성 루트 OR 검증·경로 탈출 차단)│
│    IChatHistoryService  채팅 세션 로컬 영속                        │
│    ISessionSyncService  대화 세션 서버 동기화(여러 PC 공유, 선택)   │
│    IProjectService      프로젝트(대화 묶음) 로컬 + 선택적 서버 동기화 │
│    IChatRealtimeService 실시간 메신저 파사드(REST+WS, dedup·읽음·재동기화)│
└───────────────────────────────┬──────────────────────────────────┘
                                 │ HTTP + SSE (text/event-stream) + WebSocket(/chat/ws)
                                 ▼
                  사내 AI API 서버 (OhMyAgent.AiAgent.Server)
                  활성 LLM Provider(OpenAI / Gemini / Claude / Ollama)로 중계
```

- **MVVM**: `CommunityToolkit.Mvvm` 소스 생성기(`[ObservableProperty]`, `[RelayCommand]`)
- **직렬화**: `System.Text.Json` 단일화 — 와이어용(`AgentJson.Options`)과 영속용(`PersistenceOptions`) 분리
- **컴포지션 루트**: `App.OnStartup` 에서 수동 조립(경량, 외부 DI 컨테이너 없음)

---

## 내장 도구 (27)

| 도구 | 위험도 | 설명 |
|------|--------|------|
| `read_file` / `write_file` / `edit_file` | ReadOnly / Write / Write | 파일 읽기·생성·문자열 치환 수정 |
| `list_directory` / `glob` / `grep` | ReadOnly | 디렉토리 목록 · 패턴 검색 · 내용 검색 |
| `create_directory` / `move` / `copy` / `delete` | Write / Destructive | 디렉토리 생성 · 이동 · 복사 · 삭제 |
| `run_command` | Execute | PowerShell / CMD 명령 실행(주 워크스페이스 루트 기준) |
| `get_environment` | ReadOnly | 환경변수 조회 |
| `clipboard_read` / `clipboard_write` | ReadOnly / Write | 클립보드 읽기 · 쓰기(STA 마샬링) |
| `list_processes` / `list_processes_memory_kb` | ReadOnly | 실행 프로세스 목록(이름/PID) · 메모리 사용량(KB) 포함 목록 |
| `start_process` / `kill_process` | Execute / Destructive | 프로세스 시작 · 종료 |
| `http_fetch` | Execute | 사내망 HTTP 요청(폐쇄망 내부 자원 접근) |
| `screenshot` | ReadOnly | 화면 캡처 → PNG base64(비전 모델 입력용) |
| `read_csv` / `write_csv` | ReadOnly / Write | CSV 읽기·쓰기(RFC4180 이스케이프, BCL) |
| `read_excel` / `write_excel` | ReadOnly / Write | .xlsx 읽기 · 생성·추가(ClosedXML) |
| `read_pdf` | ReadOnly | PDF 페이지별 텍스트 추출(PdfPig) |
| `read_document` | ReadOnly | Word .docx 본문 추출(BCL) |
| `manage_todos` | ReadOnly | 에이전트 작업 계획 추적(다단계 작업 분해·진행상태) — 메인 화면 계획 카드에 반영 |

> 도구 실행 결정 순서: **모델 요청 → 서버 도구 정책 게이트 → 로컬 권한 게이트(승인 카드) → 샌드박스(경로 검증) → 실행**.
> Destructive / Write / Execute 도구는 권한 모드에 따라 **실행 전 승인 카드**를 띄웁니다.
> 코어 도구는 BCL/WPF/WinForms 내장 기능만 사용하며, 문서·데이터 도구만 ClosedXML(Excel)·PdfPig(PDF) — 둘 다 순수 관리코드로 폐쇄망 적합 — 를 추가로 사용합니다.

---

## 요구 사항

- **스택**: C# **.NET 10.0** · **WPF** · **MVVM**(`CommunityToolkit.Mvvm` 소스 생성기) · `System.Text.Json`
- **OS**: Windows 10 / 11 (WPF)
- **.NET SDK**: 10.0 이상 (`dotnet --version`)
- **사내 AI API 서버**: 기본 `http://localhost:8080` ([API 계약](docs/API_CONTRACT.md))

## 빌드 & 실행

```bash
# 빌드
dotnet build OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client.csproj

# 실행 (Windows)
dotnet run --project OhMyAgent.AiAgent.Client
```

> **WSL에서 Windows용 dotnet을 쓰는 경우**: 이 프로젝트는 WPF(WinExe)라 Windows용 `dotnet.exe`로만 빌드됩니다.
> WSL에서는 Windows 실행 파일과 Windows 경로를 직접 호출하세요.
> ```bash
> "/mnt/c/Program Files/dotnet/dotnet.exe" build \
>   "C:\Users\<USERNAME>\RiderProjects\OhMyAgent.AiAgent.Client\OhMyAgent.AiAgent.Client\OhMyAgent.AiAgent.Client.csproj" \
>   -consoleloggerparameters:ErrorsOnly
> ```
> 권한 설정은 `.claude/settings.local.json`(gitignore됨) 참조 — `CLAUDE.md`의 "WSL 환경" 절.

## 첫 사용 흐름

1. **앱 시작 → 로그인 화면(LoginWindow)** 에서 **사용자 ID / 비밀번호로 로그인** → JWT 자동 발급·저장(`Authorization: Bearer`).
   (로그인 입력은 시작 게이트에만 있으며 설정창에는 없음. 설정창은 로그인 상태·로그아웃만 노출.)
2. **트레이 아이콘 → Settings** 진입 → **API 서버 주소** 확인(기본 `http://localhost:8080`)
3. **모델 선택**(서버 `/models`의 활성 Provider 기반)
4. **작업 디렉토리(워크스페이스)** 등록 — 최대 10개까지 추가하고 폴더별 접근 토글로 활성/비활성 제어
5. **권한 모드** 선택(기본 Manual 권장)
6. (선택) **프로젝트 생성** 후 사이드바에서 대화를 드래그앤드롭으로 분류, 필요 시 프로젝트별 **서버 동기화**
7. 채팅창(`Ctrl+Space`)에 목표 입력 → 에이전트 루프 실행. **+ 버튼으로 파일 첨부** 가능(전송 시 base64 인코딩, ≤10MiB)
8. 상단바 **쿼터 칩**에서 남은 사용량(일/주/월) 확인 — 새로고침 버튼으로 갱신

> **최대 토큰(MaxTokens)은 서버가 제어**합니다. 클라이언트 설정에서는 제거되었고, 와이어(`max_tokens`)에는 기본 상수만 전송됩니다.

---

## 서버 연동 (요약)

| Method | Path | 용도 |
|--------|------|------|
| `POST` | `/api/v1/auth/login` | 로그인 → JWT (`{token}`) |
| `GET`  | `/api/v1/health` | 헬스 체크 (Public) |
| `GET`  | `/api/v1/models` | 활성 Provider 기반 모델 목록 |
| `POST` | `/api/v1/agent/chat` | **에이전트 루프** — 대화기록+도구 스키마 → SSE 응답 |
| `GET`  | `/api/v1/users/me` | 로그인 사용자 프로필(이름/조직/이메일) — 설정창 읽기 전용 표시 |
| `GET`  | `/api/v1/me/quota` | 본인 토큰 쿼터(일/주/월 한도·사용·잔여) — 상단바 칩/팝업 |
| `GET`  | `/api/v1/client/version` | 클라 버전 점검(`latest/minimum_supported/...`) — 업데이트 알림 |
| `GET`  | `/api/v1/tools/policy` | 도구 정책(`mode/enabled/disabled`) — **노출+실행** 통제, 로그인 시 캐시 |
| `POST` | `/api/v1/tools/authorize` | (realtime) 도구 1회 인가(`{tool,arguments}` → `{allowed,reason}`) |
| `GET`  | `/api/v1/security/command-policy` | 서버 추가 위험명령/경로 차단 패턴 — 클라 디폴트에 **더해짐**(2중 안전) |
| `GET/PUT/DELETE` | `/api/v1/agent/sessions[/{id}]` | 대화 세션 서버 동기화(목록/단건/업서트/삭제) — *선택* |
| `GET/POST` | `/api/v1/projects` | 프로젝트 목록 조회 / 업서트(`client_id` 기준) — *선택적 동기화* |
| `POST/DELETE` | `/api/v1/projects/{id}/conversations[/{cid}]` | 대화 업서트(push) / 삭제 — *선택적 동기화* |
| `GET/POST/PATCH/DELETE` | `/api/v1/chat/rooms[…]`, `/chat/unread`, `/chat/mentions`, `/chat/attachments[…]` | **실시간 메신저** REST — 방/메시지/읽음/멤버/presence/멘션/첨부 |
| `GET` | `/api/v1/chat/rooms/{id}/members?detail=1` | 멤버 **이름 포함**(id/username/display_name) — 방 멤버 누구나, UUID→이름 해석 |
| `GET` (WS) | `/api/v1/chat/ws` | **메신저 WebSocket** — message/typing/read/presence/member 이벤트(자동 재연결) |

> 프로필·쿼터·버전·도구정책·세션/프로젝트 동기화 엔드포인트는 모두 **graceful fallback** 으로 다룹니다.
> 미구현(404/501)·오프라인이면 해당 UI만 비활성(프로필은 OS 사용자명 폴백, 쿼터 칩 숨김, 정책 없음=전체 허용,
> 버전 알림 생략)되고 앱은 로컬 전용으로 정상 동작합니다. 상세는 `docs/server-*.md` 요구 명세 참조.

**SSE 이벤트**: `message_start` · `content_delta{delta}` · `tool_call{id,name,arguments}` · `message_stop{stop_reason,usage}` · `error`.
`stop_reason == tool_use` 면 클라이언트가 도구를 실행해 `tool` 메시지로 재요청(루프 지속), `end_turn` 이면 종료.
`tool_call.arguments` 는 JSON 문자열로 주고받으며 클라이언트가 객체로 복원합니다.

상세: [`docs/AGENT_ARCHITECTURE_PLAN.md`](docs/AGENT_ARCHITECTURE_PLAN.md) · [`docs/API_CONTRACT.md`](docs/API_CONTRACT.md)

> **연결 ≠ 로그인**: `/health` 는 인증이 필요 없으므로(Public) 로그인 전에도 서버에 "연결"은 됩니다.
> 앱은 연결 상태와 인증 상태를 구분하여 `Disconnected`(서버 다운) / `Unauthenticated`(로그인 필요) /
> `Ready`(사용 가능) 3단계로 안내합니다.

---

## 문제 해결 (Troubleshooting)

| 증상 | 원인 | 해결 |
|------|------|------|
| **로그인 화면으로 자동 회귀**(모든 창 닫힘) | 로그인 전이거나 세션 중 **401**(토큰 만료/무효) 또는 로그아웃 | 로그인 화면에서 재로그인하면 메인 복귀 (401에서만 발생) |
| 배너에 **"서버 연결 실패"** | 서버 미실행 / 주소 오류 | 서버 기동 확인, 설정의 **API 서버 주소** 확인 후 **[다시 시도]** |
| 모델 칩이 비어 있음 | 미로그인(`/models` 401) 또는 활성 Provider 없음 | 로그인 후 새로고침 / 서버에 활성 LLM Provider 등록 |
| **"사용 한도를 초과했습니다"** | 토큰 쿼터(일/주/월) 소진 — **429**, 로그아웃 아님 | 한도 리셋 대기(일=자정 UTC) 또는 관리자에게 한도 상향 요청. 쿼터 칩에서 잔여 확인 |
| **"로그인은 됐지만 서버 인증에 실패"** | 로그인은 성공했으나 보호 엔드포인트가 토큰 거부 | 잠시 후 재시도(루프 방지로 메인 진입 보류). 서버/계정 권한 확인 |

---

## 설정 / 데이터 위치

| 항목 | 경로 |
|------|------|
| 설정 | `%APPDATA%/OhMyAgent/settings.json` |
| 채팅 세션 | `%APPDATA%/OhMyAgent/sessions/{id}.json` |
| 프로젝트(대화 묶음) | `%APPDATA%/OhMyAgent/projects/{id}.json` |
| 감사 로그(예정) | `%APPDATA%/OhMyAgent/audit/` |

주요 설정: `ServerBaseUrl`, `AuthToken`(로그인 시 자동), `ModelId`, `WorkspaceRoot`(주 루트),
`Workspaces`(멀티루트 목록, 폴더별 `Enabled` 토글, 최대 10), `PermissionMode`(Manual/AutoSafe/FullAuto),
`MaxIterations`(기본 25), `Hotkey`. `SchemaVersion`은 `5`(v4→v5 마이그레이션에서 `WorkspaceRoot`를
`Workspaces` 단일 항목으로 승격, `MaxTokens` 설정 제거).

---

## 프로젝트 구조

> 트리 루트는 **저장소 루트**. `docs/`·`CHANGELOG.md`·`README.md`는 저장소 루트에 있고, WPF 소스는 하위 프로젝트 폴더에 있다.

```
<repo-root>/
├── README.md · CHANGELOG.md · CLAUDE.md
├── docs/                     AGENT_ARCHITECTURE_PLAN · API_CONTRACT · tool-system(도구 설계) ·
│                             realtime-chat(메신저) · server-*.md(쿼터/버전/도구정책/명령보안) ·
│                             design-tokens · 발표자료(presentation_*.html)
└── OhMyAgent.AiAgent.Client/          (WPF 프로젝트)
    ├── App.xaml(.cs)         컴포지션 루트 · 트레이 · 핫키 · 통합 로그인 게이트(ReturnToLogin)
    ├── MainWindow.xaml(.cs)  메인 셸 · 프로젝트 사이드바(드래그앤드롭 분류) · 쿼터 칩 · 업데이트 배너
    ├── Models/               AppSettings · WorkspaceFolder · ProjectRecord/Summary · UserProfile ·
    │   │                     ChatSessionRecord/Summary · Attachment · Suggestion
    │   ├── Agent/            Agent DTO(AgentMessage/ToolCall/Usage/QuotaInfo/ClientVersionInfo/
    │   │                     RemoteProject/RemoteSession/ToolPolicy …)
    │   ├── Chat/             메신저 DTO(ChatDtos) · WS envelope(+ChatJson) · ChatEnums
    │   └── Integrity/        무결성 매니페스트(IntegrityManifest/Entry)
    ├── Services/             AgentOrchestrator · AgentApiClient · ToolRegistry · PermissionService ·
    │   │                     ToolPolicyService · ProjectService · ChatHistoryService · SessionSyncService ·
    │   │                     AppVersion · UserErrorMessages · FileAttachmentService · 워크스페이스/보안
    │   ├── Tools/            27개 ITool 구현(코어 26 + 작업계획 manage_todos)
    │   └── Chat/             IChatApiClient(REST) · IChatSocketClient(WS) · IChatRealtimeService(파사드) ·
    │                         ChatMessengerCoordinator · JwtIdentity(식별자) · ChatApiException
    ├── ViewModels/           AgentSessionViewModel · SettingsViewModel · ProjectsViewModel ·
    │   │                     WorkspaceFolderViewModel · QuotaWindowViewModel · LoginViewModel · IntegrityViewModel
    │   └── Chat/             ChatMessengerViewModel(셸) · ChatRooms/ChatRoom/ChatMessage · 멤버/멘션 VM
    ├── Views/                LoginWindow · ChatOnlyWindow · SettingsWindow · IntegrityWindow
    │   └── Chat/             ChatMessengerWindow · ChatRooms/ChatRoomView · RoomMembers/MentionFeed · Controls(말풍선/멘션)
    ├── Resources/            Colors · Tokens(디자인 토큰) · Styles · Converters · TranscriptTemplates
    └── docs/                 api-conformance-report(API 정합성 리포트)
```

---

## 보안 모델

- **멀티루트 경로 샌드박스**: 파일/셸 도구는 활성 워크스페이스 루트 **밖** 접근 차단(`IWorkspaceContext.ResolvePath` / `IsInsideWorkspace`). 여러 루트 중 하나라도 내부면 통과(OR 검증), 비활성 폴더는 차단
- **서버 도구 정책 게이트**: 도구 실행을 서버 정책(cached/realtime)으로 1차 통제 — 차단 시 승인·실행을 건너뛰고 사유를 모델에 피드백
- **권한 게이트**: 위험도(ToolRisk: ReadOnly/Write/Execute/Destructive) × 권한 모드로 실행 전 승인 결정
- **명령 검증**: `SecurityValidator` 가 위험 명령 차단 — **클라 내장 디폴트 ∪ 서버 추가 패턴**(2중 안전, 서버는 추가만·디폴트 제거 불가). 서버 정규식은 타임아웃·검증으로 ReDoS 방어
- **배포 무결성**: SHA-256 매니페스트 + Authenticode + HMAC 서명 검증

---

## 라이선스 / 배포

사내 배포용 내부 프로젝트입니다. 외부 배포 전 코드 서명 및 무결성 매니페스트 갱신이 필요합니다.
