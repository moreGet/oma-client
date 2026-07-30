# 에이전트 간 통신(A2A) + 레지스트리 계약

> 헤드리스 에이전트(`OhMyAgent.AiAgent.Host`)들이 서로를 **발견하고 위임**하기 위한 계약 문서.
> **Go 서버(`OhMyAgent.AiAgent.Server`)가 이 계약의 제공자(source of truth)**, C# 클라이언트/Host가 소비자다.
> 계약을 바꾸면 양쪽 저장소를 함께 갱신해야 한다.

## 한 줄 요약

**Go 서버 = 전화번호부(등록소)**, 실제 대화 = **에이전트 간 직접 A2A**.
에이전트는 기동 시 자기를 서버에 등록하고 heartbeat로 살아있음을 알린다. 작업 중 "이건 다른
에이전트가 낫다"고 판단하면 서버에 capability를 조회해 동료를 찾고, 서버가 발급한 **대상별 단명
토큰**으로 그 에이전트의 엔드포인트를 직접 호출한다.

동작 흐름: **등록 → 발견 → 토큰 발급 → A2A 직접 호출 → 결과 종합**

---

## 1. 레지스트리 REST API

모든 엔드포인트는 Go 서버 base URL(`OHMYAGENT_SERVER_URL`) 기준이며, 기존 chat과 동일한
**JWT Bearer** 인증을 쓴다.

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
권장값: heartbeat_interval **15s**, lease_ttl **45s**(= 3×interval).
register/heartbeat 응답의 두 값이 클라이언트 루프 주기를 결정한다.

---

## 2. A2A 인증 — 서버 토큰 브로커 (v1)

- **알고리즘: ES256(ECDSA P-256) compact JWT.** Go stdlib(`crypto/ecdsa`)와 .NET BCL(`ECDsa`)
  양쪽에서 외부 의존성 없이 구현 가능(Ed25519는 .NET BCL 미지원이라 배제).
- **클레임**: `iss`=`"ohmyagent-server"` · `sub`=호출자 member id · `cid`=호출자 agent_id(등록된 경우) ·
  `aud`=**대상 agent_id** · `iat`/`exp`(기본 **120s**) · `jti`(uuid). 헤더에 `kid`.
- **수신 검증(대상 에이전트, 로컬 — 서버 왕복 없음)**: 캐시된 공개키로 서명 검증 →
  `aud` == 자기 agent_id → `exp`/`iat` 시계 오차 허용 ±60s → `iss` 확인. 실패 시 401.
  재생 방지 캐시는 v1 미구현(120s 창 허용, `jti`는 로그 상관관계용).
- **호출 흐름**: 발견 → `POST /agents/{target}/token` 발급 → `Authorization: Bearer <token>` +
  `X-A2A-Hop` 으로 대상 호출. 대상별 단명 토큰이라 유출 피해 반경이 최소화된다.
- **키 관리**: 서버가 P-256 키쌍 생성·영속(개인키는 `APP_ENCRYPTION_SECRET` AES-GCM 암호화 저장).
  v1은 단일 활성 키, 회전은 수동(새 kid 발급 시 수신측이 재취득으로 추종).

### 수신 모드 (`OHMYAGENT_A2A_MODE`)

| 모드 | 동작 |
|------|------|
| `broker` | 브로커 발급 ES256 토큰 검증. **레지스트리 사용 시 기본** |
| `token` | 공유 Bearer 토큰 상수시간 비교 — 레지스트리 없는 개발/폐쇄 환경 폴백 |
| `anon` | 인증 없음(옵트인) |

`broker` 모드는 자기 agent_id가 필요하므로 **등록 성공이 전제** — 등록 실패 상태의 broker 모드는
A2A 수신을 401로 거부하고 로그에 이유를 남긴다.

**보안 필수 조건**: `alg` 화이트리스트는 `ES256`만 — 다른 alg·`none`은 즉시 거부(alg 혼동 공격).
서명 검증 전에 클레임을 신뢰하지 않는다. JWT ES256 서명은 r‖s 64바이트 raw(IEEE P1363)이며
DER이 아니다.

---

## 3. 협업 도구

| 도구 | args | Risk | 동작 |
|------|------|------|------|
| `discover_agents` | `capability?` · `query?` · `status?` | **Read** | 레지스트리 조회 → `[{agent_id, name, capabilities, status}]` 반환. 자기 자신은 `exclude_self`로 제외 |
| `ask_agent` | `agent_id?` · `capability?` · `prompt` | **Execute**(승인 게이트) | 대상 해석 → 단명 토큰 발급 → `POST {endpoint}/api/v1/agent/chat` SSE 호출 → 응답 텍스트 반환 |

`ask_agent` 제약:
- **엔드포인트는 반드시 레지스트리에서 해석** — args에 raw URL 필드를 두지 않는다(SSRF 방지).
- `X-A2A-Hop` 헤더에 수신 요청의 홉 + 1을 실어 순환 위임을 방어(초과 시 508).
- 토큰 수명이 120s이므로 **호출 직전 발급**(캐시하지 않고 재호출 시 재발급).

---

## 4. 실행 env (Host)

| env | 의미 | 기본 |
|-----|------|------|
| `OHMYAGENT_SERVER_URL` · `OHMYAGENT_AUTH_TOKEN` | 사내 AI 서버 주소 · JWT | — |
| `OHMYAGENT_LISTEN` | **A2A 리스너 모드** 활성 (예: `http://0.0.0.0:8080/`) | off |
| `OHMYAGENT_AGENT_NAME` | 레지스트리 표시 이름 | 호스트명 |
| `OHMYAGENT_ADVERTISE_URL` | 다른 에이전트가 접속할 내 공개 URL | LISTEN에서 유도. **`0.0.0.0` 이면 필수**(없으면 기동 거부) |
| `OHMYAGENT_CAPABILITIES` | capability csv | 빈 목록 |
| `OHMYAGENT_REGISTRY` | 등록 on/off | LISTEN 설정 시 `on` |
| `OHMYAGENT_A2A_MODE` | 수신 인증 모드 | 레지스트리 on이면 `broker`, off면 `token` |
| `OHMYAGENT_A2A_TOKEN` | `token` 모드 폴백용 공유 Bearer | — |

리스너 모드에서만 레지스트리 생명주기가 돈다. 원샷(`OHMYAGENT_PROMPT`)·대화 모드는 영향 없다.

---

## 5. 구현 위치

| 역할 | 파일 |
|------|------|
| 레지스트리 HTTP 클라이언트 | `Core/Services/Registry/AgentRegistryClient.cs` · `IAgentRegistryClient.cs` |
| A2A 대상 호출(SSE) | `Core/Services/Registry/A2aChatClient.cs` |
| 계약 DTO | `Core/Models/Registry/RegistryModels.cs` |
| 등록·heartbeat·해제 생명주기 | `Host/AgentRegistryLifecycle.cs` · `RegistryHeartbeatPolicy.cs` · `AgentRegistryOptions.cs` |
| 수신 인증(모드 분기) | `Host/A2aInboundAuthenticator.cs` · `A2aAuth.cs` |
| 브로커 토큰 검증(순수 함수) | `Host/A2aBrokerToken.cs` |
| 리스너·홉·SSE 출력 | `Host/A2aListener.cs` · `A2aHop.cs` · `A2aSseWriter.cs` · `A2aRequest.cs` |

### 설계 제약

- Host는 **BCL만** 사용한다 — ASP.NET Core·외부 JWT 라이브러리 금지. Core만 참조.
- 등록 실패가 **리스너 자체를 죽이면 안 된다** — 레지스트리 없이도 A2A 수신은 가능해야 한다
  (등록은 발견 편의일 뿐). 단 `broker` 수신 모드는 등록 성공이 전제.
- heartbeat 실패는 로그 후 다음 주기 재시도, 404(lease 만료)는 재-register로 자가 치유.
