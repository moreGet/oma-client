# 구현 프롬프트 ① — C# 클라이언트/Host (에이전트 레지스트리 연동 + 에이전트 간 협업 도구)

> **이 프롬프트를 언제 쓰나:** OhMyAgent.AiAgent.Client 저장소(`/mnt/c/Users/dkdlw/RiderProjects/OhMyAgent.AiAgent.Client`)에서
> Claude에게 그대로 붙여넣어 실행시킨다. 코드 생성이 포함되므로 **반드시 `wpf-orchestrator` 하네스로 실행**한다(프로젝트 규약).
>
> **짝 문서:** Go 서버 쪽 구현은 `PROMPT-go-server.md`. 아래 **§공유 계약(SYNC)** 블록은 두 파일에 동일하게 존재하며,
> 하나를 고치면 반드시 다른 하나도 고쳐야 한다. 서버가 이 계약대로 구현돼 있어야 이 클라이언트 작업이 동작한다.

---

## 배경 (이미 완료된 것)

- `OhMyAgent.AiAgent.Core`(net10.0, 크로스플랫폼) — 통신·오케스트레이터·도구·보안. `AgentApiClient`가 `/api/v1/agent/chat` SSE 계약의 클라이언트.
- `OhMyAgent.AiAgent.Host`(net10.0 콘솔, Core만 참조) — 헤드리스 에이전트. `OHMYAGENT_LISTEN` 설정 시 `A2aListener`(BCL HttpListener)가 자기 오케스트레이터를 **동일한 `/api/v1/agent/chat` SSE 계약으로 대칭 노출**. `A2aAuth`(Bearer, 상수시간 비교), `A2aHop`(X-A2A-Hop 순환 방어, 508), `SemaphoreSlim` 동시 제한(429) 이미 존재.
- 백엔드 = **별도 Go 저장소** `OhMyAgent.AiAgent.Server`(AI 모델 중계 서버). JWT(HS256) 인증, 헥사고날. 클라/Host가 `OHMYAGENT_SERVER_URL` + `OHMYAGENT_AUTH_TOKEN`(JWT)로 접속.

## 목표

각 헤드리스 에이전트가 **질문에 따라 스스로 협업**하게 만든다:

1. **등록** — 에이전트가 리스너 모드로 뜨면 자기를 Go 서버 레지스트리에 등록(이름·엔드포인트·capabilities)하고 주기적 heartbeat로 살아있음을 알린다. 종료 시 해제.
2. **발견** — 에이전트가 작업 중 "이건 다른 에이전트가 낫다"고 판단하면, Go 서버에 **누가 그 capability를 갖고 있는지 조회**한다.
3. **대화·수행** — 발견한 에이전트의 엔드포인트로 **A2A 직접 호출**(기존 SSE 계약)해 작업을 위임하고 결과를 받아 종합한다.

즉 Go 서버 = **전화번호부(등록소)**, 실제 대화 = **에이전트 간 직접 A2A**.

---

## §공유 계약(SYNC) — 레지스트리 REST API

> **이 블록은 `PROMPT-go-server.md`와 100% 동일해야 한다.** Go 서버가 이 스펙대로 구현한다. 클라이언트는 이 스펙의 소비자.
> 모든 엔드포인트는 Go 서버 base URL(C# 쪽 `OHMYAGENT_SERVER_URL`) 기준, 기존 chat과 동일한 **JWT Bearer** 인증(서버 구현: `router.Secured` + `security.MinRole`).

### 데이터 모델 — Agent 레코드
| 필드 | 타입 | 설명 |
|------|------|------|
| `agent_id` | string(uuid) | 서버 발급. register 응답으로 반환 |
| `name` | string | 소유자 범위 내 고유. 사람이 읽는 식별자 |
| `endpoint_url` | string | A2A 리스너 base URL (예: `http://10.0.0.5:8080`). 호출자가 여기에 `/api/v1/agent/chat` 붙여 접속 |
| `capabilities` | string[] | 자유 태그 (예: `["code-review","korean-nlp"]`). 발견 필터 키 |
| `tags` | string[] | (선택) 환경/그룹 태그 (예: `["prod","gpu"]`) |
| `model` | string | (선택) 주 모델 id |
| `version` | string | (선택) Host 버전 |
| `status` | enum | **서버가 계산**: `online` / `stale` / `offline` (heartbeat 경과 기준) |
| `last_heartbeat_at` | RFC3339 | 마지막 heartbeat 시각 |

### 엔드포인트
1. **`POST /api/v1/agents/register`** (JWT User+)
   - req: `{ "name", "endpoint_url", "capabilities":[], "tags":[]?, "model"?, "version"? }`
   - resp: `{ "agent_id", "lease_ttl_seconds", "heartbeat_interval_seconds" }`
   - **Upsert**: 같은 `(owner_member_id, name)` 재등록이면 기존 `agent_id` 유지하고 endpoint/capabilities 갱신(재기동 시 중복 방지).
2. **`POST /api/v1/agents/{id}/heartbeat`** (JWT, 소유자만)
   - resp: `{ "status":"online", "lease_ttl_seconds" }`
   - 소유자 아니거나 없는 id(리스 만료로 서버가 정리한 경우 포함)면 **404** → 클라이언트는 **재-register**로 자가 치유.
3. **`DELETE /api/v1/agents/{id}`** (JWT, 소유자만) — 우아한 해제. resp 204.
4. **`GET /api/v1/agents`** (JWT User+) — 발견
   - query: `?capability=<tag>` · `?tag=<tag>` · `?status=online` · `?q=<freetext>` · `?exclude_self=<agent_id>`
   - 기본은 `online`+`stale` 반환, `?status=online`이면 online만.
   - resp: `{ "agents":[ { "agent_id","name","endpoint_url","capabilities","tags","model","status","last_heartbeat_at" } ] }`
5. **`GET /api/v1/agents/{id}`** (JWT User+) — 단건. 404 가능.
6. **`POST /api/v1/agents/{id}/token`** (JWT User+) — **A2A 호출 토큰 발급(브로커)**. `{id}` = 호출 **대상** 에이전트
   - req 본문 없음. resp: `{ "token", "expires_in_seconds", "audience_agent_id" }` · 대상 미존재 404. (대상별 호출 ACL은 v2)
7. **`GET /api/v1/agents/a2a-public-key`** (JWT User+) — 수신측 서명 검증용 공개키
   - resp: `{ "kid", "alg":"ES256", "public_key_pem" }` · 수신 에이전트는 기동 시 1회 취득·캐시, 미지의 `kid` 토큰 수신 시 1회 재취득(키 회전 추종)

### 생존성(liveness) — 서버가 read 시 계산
`online`: `now - last_heartbeat_at < lease_ttl` · `stale`: `< 3×lease_ttl` · 그 이상 `offline`.
권장값: heartbeat_interval **15s**, lease_ttl **45s**(= 3×interval). (register/heartbeat 응답의 두 값이 클라이언트 루프 주기를 결정)

### A2A 인증(에이전트→에이전트) — v1: 서버 토큰 브로커
- **알고리즘: ES256(ECDSA P-256) compact JWT.** Go stdlib(`crypto/ecdsa`)와 .NET BCL(`ECDsa`) 양쪽에서 외부 의존성 없이 구현 가능(Ed25519는 .NET BCL 미지원이라 배제).
- **클레임**: `iss`=`"ohmyagent-server"` · `sub`=호출자 member id · `cid`=호출자 agent_id(등록된 경우) · `aud`=**대상 agent_id** · `iat`/`exp`(기본 **120s**) · `jti`(uuid). 헤더에 `kid`.
- **수신 검증(대상 에이전트, 로컬 — 서버 왕복 없음)**: 캐시된 공개키로 서명 검증 → `aud` == 자기 agent_id(register 응답으로 알고 있음) → `exp`/`iat` 시계 오차 허용 ±60s. 실패 시 401. 재생 방지 캐시는 v1 미구현(120s 창 허용, `jti`는 로그 상관관계용).
- **호출 흐름**: 발견 → `POST /agents/{target}/token` 발급 → `Authorization: Bearer <token>` + `X-A2A-Hop` 으로 대상 호출. 대상별 단명 토큰이라 유출 피해 반경이 최소화된다.
- **키 관리**: 서버가 P-256 키쌍 생성·영속(개인키는 기존 `APP_ENCRYPTION_SECRET` AES-GCM 방식으로 암호화 저장). v1은 단일 활성 키, 회전은 수동(새 kid 발급 시 수신측이 재취득으로 추종).
- **수신 모드**: `OHMYAGENT_A2A_MODE` = `broker`(레지스트리 사용 시 기본) | `token`(기존 공유 토큰 상수시간 비교 — 레지스트리 없는 개발/폐쇄 환경 폴백) | `anon`(기존 ANON 옵트인). broker 모드는 자기 agent_id가 필요하므로 **등록 성공이 전제** — 등록 실패 상태에서 broker 모드면 A2A 수신 401(로그로 이유 명시).

---

## 클라이언트/Host 구현 작업 (이 저장소)

### A. `AgentRegistryClient` (Core 신규)
- `OhMyAgent.AiAgent.Core/Services/Registry/`에 `IAgentRegistryClient` + `AgentRegistryClient`.
- 기존 `AgentApiClient`처럼 `ISettingsService`의 `ServerBaseUrl`+`AuthToken`(JWT)을 써서 위 §공유 계약 5개 엔드포인트 호출. HttpClient는 Program.cs가 주입(기존 것 재사용).
- 메서드: `RegisterAsync(RegisterRequest)→RegisterResponse`, `HeartbeatAsync(agentId)→HeartbeatResponse`(404 → typed `AgentLeaseExpired`), `DeregisterAsync(agentId)`, `DiscoverAsync(DiscoverQuery)→IReadOnlyList<AgentDescriptor>`, `GetAsync(agentId)`, **`MintA2aTokenAsync(targetAgentId)→A2aToken`**(§계약 6), **`GetA2aPublicKeyAsync()→A2aPublicKey`**(§계약 7).
- 모델(record)은 Core `Models/Registry/`. JSON 필드명은 §공유 계약과 정확히 일치(snake_case; 기존 `AgentJson.Options` 관례 따를 것 — 실측 확인).

### B. Host 생명주기 배선 (`Program.cs` / 신규 `AgentRegistryLifecycle`)
- **리스너 모드에서만**(`OHMYAGENT_LISTEN` 설정 시) 동작. 원샷/대화 모드는 불변.
- 리스너 기동 성공 후:
  1. `RegisterAsync` — `name`=`OHMYAGENT_AGENT_NAME`, `endpoint_url`=`OHMYAGENT_ADVERTISE_URL`(미설정 시 `OHMYAGENT_LISTEN`에서 유도하되 `0.0.0.0`은 광고 불가 → 명시 필수·없으면 기동 거부), `capabilities`=`OHMYAGENT_CAPABILITIES`(csv), `model`/`version` 자동.
  2. 등록 성공 직후 `GetA2aPublicKeyAsync`로 **브로커 공개키 취득·캐시**(broker 수신 검증 준비 — §C-2).
  3. **heartbeat 백그라운드 루프** — `heartbeat_interval_seconds`마다 `HeartbeatAsync`. 404(`AgentLeaseExpired`)면 재-register. 네트워크 실패는 로그 후 다음 주기 재시도(죽지 않음).
  4. 종료(SIGTERM/Ctrl+C, 기존 `ConsoleCancellation` 취소 토큰)에 `DeregisterAsync` best-effort.
- 등록 실패가 **리스너 자체를 죽이면 안 된다** — 레지스트리 없이도 A2A 수신은 계속 가능해야 함(등록은 발견 편의일 뿐). 실패는 경고 로그.

### C. 협업 도구 2종 (Core 신규 `Services/Tools/`, Host 레지스트리에 등록)
1. **`discover_agents`** — args `{ "capability"?: string, "query"?: string, "status"?: "online" }`.
   `AgentRegistryClient.DiscoverAsync` 호출 → `[{agent_id, name, capabilities, status}]`를 도구 결과로 반환(모델이 고르게). 자기 자신은 `exclude_self`로 제외. Risk = **Read**.
2. **`ask_agent`** — args `{ "agent_id"?: string, "capability"?: string, "prompt": string }`.
   - `agent_id` 있으면 `GetAsync`로 엔드포인트 해석. 없고 `capability`만 있으면 `DiscoverAsync`로 online 후보 중 첫 번째 선택(선택 로직·모호 시 실패 메시지 명확히).
   - **토큰 발급**: 해석한 대상에 대해 `MintA2aTokenAsync(targetAgentId)`로 단명 ES256 토큰을 받는다(§계약 6). 발급 실패(404 = 대상 소멸)는 명확한 도구 오류로.
   - 해석한 `endpoint_url`로 **A2A 호출**: `POST {endpoint}/api/v1/agent/chat` (SSE), `Authorization: Bearer <발급 토큰>`, **`X-A2A-Hop` 헤더에 현재 홉+1**(수신 요청의 홉을 이어받아 순환 방어). content_delta를 모아 최종 텍스트를 도구 결과로 반환. 토큰 수명이 120s이므로 **호출 직전 발급**(캐시 불필요 — 재호출 시 재발급).
   - Risk = **Execute**(승인 게이트 대상). **엔드포인트는 반드시 레지스트리에서 해석** — 모델이 임의 URL을 직접 넣지 못하게(SSRF 방지). args에 raw url 필드 두지 말 것.
- **A2A 호출 클라이언트**: `AgentApiClient.SendAsync`는 `settings.ServerBaseUrl` 고정이라 임의 엔드포인트 호출 불가. 기존 SSE 리더/`Dispatch` 파싱(RoundTrip 테스트가 잠근 그 경로)을 재사용하는 얇은 `A2aChatClient(endpoint, token)`를 Core에 만들거나 `AgentApiClient`를 per-call base URL 파라미터화 — 실측 후 최소 변경 방향 택일.

### C-2. 수신측 브로커 검증 (`A2aAuth` 확장, Host)
- 기존 `A2aAuth`(공유 토큰 상수시간 비교)를 **모드 분기**로 확장: `OHMYAGENT_A2A_MODE` = `broker`(레지스트리 사용 시 기본) | `token`(기존 경로 그대로) | `anon`(기존 ANON).
- **broker 모드 검증(BCL만, 외부 JWT 라이브러리 금지)**: compact JWT를 직접 파싱 — base64url 디코드(헤더/페이로드/서명), 헤더 `alg`가 정확히 `ES256`인지 확인(**다른 alg·`none`은 즉시 거부 — alg 혼동 공격 방지**), 캐시된 공개키(`ECDsa.ImportFromPem` 또는 `ImportSubjectPublicKeyInfo`)로 `header.payload` 서명 검증(IEEE P1363 vs DER 형식 주의 — JWT ES256은 r‖s 64바이트 raw), 클레임 검증: `aud`==자기 agent_id, `exp`/`iat` ±60s, `iss`=="ohmyagent-server".
- 공개키는 기동 시(등록 직후) `GetA2aPublicKeyAsync`로 취득·캐시. 토큰의 `kid`가 캐시와 다르면 **1회 재취득** 후 재검증(키 회전 추종), 그래도 다르면 401.
- 등록 실패 상태에서 broker 모드면 자기 agent_id를 모르므로 A2A 수신은 401 + 로그로 이유 명시(§공유 계약). 검증 로직은 순수 정적 메서드(`A2aBrokerToken.Validate(jwt, publicKeyPem, ownAgentId, now)`)로 추출해 시간 주입 가능하게 — 테스트에서 만료/미래 iat 케이스를 시계 없이 잠근다(기존 `ResolveShell` 순수화 패턴).

### D. env 추가 (`HeadlessConfig` 확장)
| env | 의미 | 기본 |
|-----|------|------|
| `OHMYAGENT_AGENT_NAME` | 레지스트리 표시 이름 | 호스트명 |
| `OHMYAGENT_ADVERTISE_URL` | 다른 에이전트가 접속할 내 공개 URL | (LISTEN에서 유도, 0.0.0.0이면 필수) |
| `OHMYAGENT_CAPABILITIES` | capability csv | 빈 목록 |
| `OHMYAGENT_REGISTRY` | 등록 on/off | LISTEN 설정 시 `on` |
| `OHMYAGENT_A2A_MODE` | 수신 인증 모드 `broker`/`token`/`anon` | 레지스트리 on이면 `broker`, off면 `token` |
| `OHMYAGENT_A2A_TOKEN` | (기존) `token` 모드 폴백용 공유 Bearer | — |

### E. 제약·보안
- Host는 계속 **BCL만**(ASP.NET Core·외부 JWT 라이브러리 금지), Core만 참조. Windows 동작·기존 테스트 무회귀.
- `ask_agent`는 Execute 게이트 + hop 방어 + 레지스트리 해석 강제 + **대상별 단명 토큰**(브로커). `discover_agents`는 정보 노출뿐이라 Read.
- broker 검증에서 `alg` 화이트리스트(ES256만)·`none` 거부는 필수(alg 혼동 공격). 서명 검증 전에 클레임을 신뢰하지 말 것.
- 레지스트리/heartbeat 실패는 **graceful degrade**(A2A 수신·로컬 작업은 계속 — 단 broker 모드 수신은 등록 성공 전제).

### F. 테스트
- `AgentRegistryClient` 요청/응답 직렬화(§공유 계약 필드명 정합), 404→재register 트리거, MintToken/PublicKey 파싱.
- heartbeat 루프: 실패 후 재시도·lease 만료 시 재등록(시간은 주입 가능한 추상화로 — 기존 `ResolveShell`류 순수화 패턴 참고).
- **`A2aBrokerToken.Validate` 집중 테스트**: 자체 생성 P-256 키쌍으로 유효/만료/미래 iat/`aud` 불일치/서명 훼손/`alg=none`/`alg=HS256` 위장/미지 `kid` 케이스. .NET `ECDsa`로 테스트 토큰을 직접 서명해 생성(서버 없이 검증 로직 잠금).
- `ask_agent` 홉 증가·엔드포인트 해석·capability 모호성 실패·토큰 발급 실패 처리. `discover_agents` 필터.
- Windows 러너 그대로. 실기 A2A 왕복(브로커 발급→수신 검증 포함)은 WSL 스모크로 별도 검증(오케스트레이터가 수행).

### G. 검증 게이트 (전부 실측 — Windows dotnet)
(a) 전체 빌드 0경고/0오류 (b) 전체 테스트 그린(기존 + 신규) (c) `dotnet publish OhMyAgent.AiAgent.Host -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true` 성공.

## 실행 방식
- **`wpf-orchestrator` 하네스로 실행**: Architect(설계·경계) → ServiceEngineer(구현) → QAReviewer(독립 검증). ViewModel/UI Phase는 해당 없음 → 생략.
- **커밋하지 말 것**(별도 지시 전까지 워킹트리 유지). Go 서버가 §공유 계약을 먼저(또는 병행) 구현해야 실기 왕복이 가능 — 그때까지는 클라이언트 단위 테스트로 검증.

## 완료 정의
- 리스너 모드 Host가 기동 시 자기를 등록하고 heartbeat 유지, 종료 시 해제.
- 에이전트가 `discover_agents`로 동료를 찾고, **브로커에서 대상별 단명 토큰을 발급받아** `ask_agent`로 위임·결과 종합.
- 수신측이 서버 공개키만으로(서버 왕복 없이) 토큰을 검증하고, `aud` 불일치·만료·위조 토큰을 401로 거부.
- 목표 시나리오: "질문 → Go 서버에서 적합 에이전트 발견 → 브로커 토큰으로 A2A 대화 → 작업 수행"이 코드로 성립.
