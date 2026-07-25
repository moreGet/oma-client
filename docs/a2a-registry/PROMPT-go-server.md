# 구현 프롬프트 ② — Go 서버 (에이전트 레지스트리: 등록 · 발견 · 생존성 · 어드민)

> **이 프롬프트를 언제 쓰나:** Go 백엔드 저장소 `OhMyAgent.AiAgent.Server`
> (경로: `C:\Users\dkdlw\GolandProjects\OhMyAgent.AiAgent.Server`, WSL: `/mnt/c/Users/dkdlw/GolandProjects/OhMyAgent.AiAgent.Server`)
> 에서 Claude에게 붙여넣어 실행시킨다. 그 저장소의 `CLAUDE.md`/`TEMPLATE-SPEC.md` 규약(헥사고날, 외부 import 제한 등)을 **먼저 읽고 준수**한다.
>
> **짝 문서:** 클라이언트 쪽은 `PROMPT-client-host.md`. 아래 **§공유 계약(SYNC)** 블록은 두 파일에 동일하며,
> 하나를 고치면 반드시 다른 하나도 고쳐야 한다. **이 서버가 §공유 계약의 제공자(source of truth)** 다.

---

## 배경

이 서버는 이미 AI 모델 중계(OpenAI/Claude/Gemini/Ollama), `POST /api/v1/agent/chat`(SSE function-calling), JWT 인증, 도구 정책(`/api/v1/tools/policy`, `/admin/tools`), 어드민 콘솔(`/admin`)을 제공한다.

이제 여기에 **에이전트 레지스트리**를 추가한다: 헤드리스 에이전트(별도 C# Host 바이너리)들이 자기를 등록하고, 서로를 발견하는 전화번호부. 실제 에이전트 간 대화(A2A)는 에이전트끼리 직접 SSE로 하고, **이 서버는 등록·발견·생존성만** 담당한다(중계 relay 아님, v1 기준).

> 주의: 기존 `internal/domain/agent`는 **LLM 중계 루프** 도메인이다. 레지스트리는 **별개 도메인** `internal/domain/agentregistry`로 만들어 혼동을 피한다.

---

## §공유 계약(SYNC) — 레지스트리 REST API

> **이 블록은 `PROMPT-client-host.md`와 100% 동일해야 한다.** 이 서버가 아래를 구현하고, C# 클라이언트가 소비한다.
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

## 서버 구현 작업 (헥사고날 규약 준수)

### A. 도메인 `internal/domain/agentregistry`
- `model.go`: `Agent` 엔티티, `Status` enum, `ErrValidation`(→400)·`ErrNotFound`(→404)·`ErrForbidden`(→403, 소유자 불일치) typed 에러. **순수 로직**: `ComputeStatus(lastHeartbeat, now, leaseTTL) Status`(생존성 계산 — 여기 단위 테스트 집중). 외부 import 금지(stdlib만), TEMPLATE-SPEC §1 준수.
- `port.go`: `Repository` 포트 — `Upsert(ctx, agent) (Agent, error)`(owner+name 유니크), `Get(ctx, id) (Agent, error)`, `Touch(ctx, id, ownerID, now) error`(heartbeat), `Delete(ctx, id, ownerID) error`, `List(ctx, filter) ([]Agent, error)`.

### B. 애플리케이션 `internal/application/agentregistry`
- 유스케이스: `Register`(uuid 발급 or upsert 재사용, endpoint_url 검증 — http/https 절대 URL만, SSRF 관점 사설/링크로컬 정책은 주석으로 결정), `Heartbeat`, `Deregister`, `Discover`(필터+`ComputeStatus`로 status 채우고 status 필터 적용), `GetOne`.
- **토큰 브로커 유스케이스**: `MintToken(callerMemberID, callerAgentID?, targetAgentID)` — 대상 존재 확인(404) 후 §공유 계약 클레임(`iss/sub/cid/aud/iat/exp/jti`, 헤더 `kid`)으로 **ES256 서명 JWT** 발급. `PublicKey()` — 활성 키의 `{kid, alg, public_key_pem}` 반환. 서명은 기존 JWT 발급 코드가 쓰는 라이브러리(golang-jwt 등, 실측)로 ES256 — stdlib `crypto/ecdsa` 기반이라 신규 의존 없음.
- 소유자 판정: JWT claims의 member id를 owner로. heartbeat/delete는 owner 불일치 시 `ErrForbidden`(단, 응답은 404로 매핑해 존재 여부 은닉할지 결정 — 계약은 404).
- 설정값(heartbeat_interval, lease_ttl, token_ttl)은 config에서 주입.

### C. 어댑터 in/http `internal/adapter/in/http/agentregistry`
- 핸들러 + DTO(요청/응답 struct, JSON 태그 snake_case로 §공유 계약과 정확히 일치) + 에러 매핑(기존 `handler.go`의 `Handle`/`HandleAgent` 관례, typed 에러→상태코드).
- **라우트 등록**(`cmd/api/main.go`, 기존 패턴):
  ```go
  router.Secured("POST /api/v1/agents/register",        httpin.Handle(agentRegH.Register),    security.MinRole(domainauth.RoleLevelUser))
  router.Secured("POST /api/v1/agents/{id}/heartbeat",  httpin.Handle(agentRegH.Heartbeat),   security.MinRole(domainauth.RoleLevelUser))
  router.Secured("DELETE /api/v1/agents/{id}",          httpin.Handle(agentRegH.Deregister),  security.MinRole(domainauth.RoleLevelUser))
  router.Secured("GET /api/v1/agents",                  httpin.Handle(agentRegH.List),        security.MinRole(domainauth.RoleLevelUser))
  router.Secured("GET /api/v1/agents/{id}",             httpin.Handle(agentRegH.Get),         security.MinRole(domainauth.RoleLevelUser))
  router.Secured("POST /api/v1/agents/{id}/token",      httpin.Handle(agentRegH.MintToken),   security.MinRole(domainauth.RoleLevelUser))
  router.Secured("GET /api/v1/agents/a2a-public-key",   httpin.Handle(agentRegH.PublicKey),   security.MinRole(domainauth.RoleLevelUser))
  ```
  > 주의: `GET /api/v1/agents/a2a-public-key`와 `GET /api/v1/agents/{id}` 패턴 충돌 — Go 1.22+ ServeMux는 구체 경로가 우선이지만, 기존 라우터 구현에서 실제 우선순위를 **실측 확인**하고 필요 시 경로를 `/api/v1/a2a/public-key`로 분리(분리 시 §공유 계약 SYNC 블록 양쪽 파일 함께 갱신).
  (JWT claims 접근은 chat/quota 핸들러가 쓰는 기존 방식 실측해 동일하게.)
- 본문 상한: 기존 `http.MaxBytesReader` 관례(일반 1 MiB) 적용.

### D. 어댑터 out/db `internal/adapter/out/db`
- goose 마이그레이션 **`migrations/00026_create_agents.sql`**(다음 번호 실측 확인 — 현재 최신 00025):
  - `agents` 테이블: `id`(pk, text uuid), `owner_member_id`(fk members, 인덱스), `name`, `endpoint_url`, `capabilities`(json text), `tags`(json text), `model`, `version`, `last_heartbeat_at`, `created_at`, `updated_at`. **유니크 `(owner_member_id, name)`**. 발견 성능용 인덱스(`last_heartbeat_at`, 필요 시 capabilities 조회 전략은 json 스캔 vs 정규화 — v1은 json+앱단 필터 허용, 주석으로 트레이드오프 명시).
  - sqlite/mysql **양쪽 호환** SQL(기존 마이그레이션 스타일 따를 것).
- 레포지토리(손작성 SQL): 포트 구현. capabilities/tags는 json 마샬. `List` 필터는 SQL where + 앱단 status 계산 조합.
- **`migrations/00027_create_a2a_keys.sql`**: `a2a_keys` 테이블 — `kid`(pk), `private_key_pem_encrypted`(AES-GCM, Provider api_key 직접 저장과 동일 방식 재사용), `public_key_pem`, `active`(bool), `created_at`. 기동 시 활성 키 없으면 P-256 키쌍 생성·저장(bootstrap, Provider 키 암호화 코드 경로 실측 재사용). v1은 단일 활성 키.

### E. 어드민 콘솔 `internal/adapter/in/web` — `/admin/agents`
- 등록된 에이전트 목록 페이지(html/template + Bootstrap 다크, 기존 `/admin/*` 패턴). 컬럼: name·status 뱃지(online 녹색/stale 노랑/offline 회색)·endpoint·capabilities·model·last_heartbeat·owner. 수동 **삭제(강제 해제)** 액션. 사이드바에 메뉴 추가(기존 접이식 구조).
- 접근 역할: 조회는 admin↑(`RoleLevelAdmin`) 권장(운영 가시성). 결정은 기존 `/admin/tools`·`/admin/members` 역할 관례에 맞춰라.

### F. 설정 `internal/config` + `configs/*.yaml`
```yaml
registry:
  heartbeat_interval: "15s"
  lease_ttl: "45s"
  token_ttl: "120s"         # A2A 브로커 토큰 수명
  sweep_interval: "5m"      # (선택) offline 오래된 레코드 정리 주기. 0이면 비활성
```
- env override 관례(`APP_...`) 기존 방식 따를 것.
- (선택) `sweep_interval`마다 `offline` 초과 레코드 삭제하는 백그라운드 sweeper — 없으면 레코드가 계속 쌓이므로 최소 구현 권장(단, read 시 status 계산이 우선이라 없어도 발견 정확도엔 무해).

### G. 제약·품질
- **TEMPLATE-SPEC 규약 절대 준수**: 도메인 외부 import 금지, 레이어 방향(domain←application←adapter), 손작성 SQL, 에러 typed 매핑.
- 기존 기능·테스트 **무회귀**.
- 검증(README 관례): `go build ./...` · `go vet ./...` · `gofmt -l .`(빈 출력) · `go test ./...` 전부 그린. 새 도메인 `ComputeStatus`·유스케이스·핸들러·레포지토리 테스트 추가.
- **브로커 테스트**: MintToken이 발급한 JWT를 공개키로 검증하는 왕복(서명·`aud`·`exp`·`kid` 정합), 대상 미존재 404, 키 bootstrap(없으면 생성·재기동 시 재사용).
- `docs/API-SPEC.md`에 신규 7개 엔드포인트 명세 추가.

## 완료 정의
- C# Host가 `POST /api/v1/agents/register` → heartbeat → `GET /api/v1/agents?capability=...` → `DELETE`까지 왕복 성공.
- **토큰 브로커 왕복**: `POST /agents/{id}/token`으로 받은 ES256 JWT가 `GET /agents/a2a-public-key`의 공개키로 검증되고 `aud`가 대상 agent_id와 일치.
- `/admin/agents`에서 등록 에이전트와 실시간 status 확인.
- 생존성 계산이 heartbeat 중단 시 `online→stale→offline`으로 전이.

## 짝 문서와의 통합 확인
클라이언트(`PROMPT-client-host.md`)가 이 계약대로 `AgentRegistryClient`를 구현한다. **JSON 필드명·상태코드·status enum 문자열이 정확히 일치**해야 실기 왕복이 된다. 한쪽이라도 §공유 계약을 바꾸면 두 프롬프트를 함께 갱신할 것.
