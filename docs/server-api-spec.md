# 서버 API 요구 스펙 — 프로필 조회 + 프로젝트/대화 동기화

대상: 서버(API) 팀. 클라이언트(OhMyAgent.AiAgent.Client)가 호출할 신규 엔드포인트 명세.
인증: 모든 엔드포인트 `Authorization: Bearer <JWT>` (기존 `/api/v1/auth/login` 발급 토큰).
공통: JSON UTF-8. 시각은 ISO-8601 UTC(`2026-06-27T08:30:00Z`). 에러는 기존 envelope(`{ "error": { "code": "...", "message": "..." } }`) 재사용.

---

## A. 프로필 조회 (요구 #5) — 필수

### `GET /api/v1/users/me`
로그인한 사용자 본인의 프로필. 클라 설정창 "사용자 프로필" 카드에 표시(읽기 전용).

**응답 200**
```json
{
  "username": "shkim",
  "display_name": "김성현",
  "organization": "플랫폼개발팀",
  "email": "shkim@company.com"
}
```

| 필드 | 타입 | 필수 | 설명 |
|------|------|:---:|------|
| `username` | string | ✅ | 로그인 ID |
| `display_name` | string | ✅ | 화면 표시 이름(없으면 username 반환 권장) |
| `organization` | string\|null | ⛔ | 소속 조직/부서. 없으면 null |
| `email` | string\|null | ⛔ | 이메일. 없으면 null |

**에러**: `401`(토큰 무효/만료) — 클라는 미인증 처리. `404`/미구현 — 클라는 graceful fallback(로컬 Windows 사용자명 표시).

> 클라 동작: 설정창 진입 시 1회 호출. 실패해도 앱은 정상 동작(이름은 OS 사용자명으로 폴백).

---

## B. 프로젝트/대화 동기화 (요구 #4) — 선택적

클라는 **로컬 우선** 저장(내 PC). 사용자가 프로젝트별 "서버 동기화"를 누를 때만 아래를 호출.
서버 미구현 시 클라는 동기화 버튼을 "미지원"으로 안내하므로, **A(프로필)만 먼저, B는 나중에 구현해도 무방**.

### 데이터 모델
- **Project**: 대화 세션 여러 개를 묶는 상위 컨테이너. (작업 디렉토리는 포함하지 않음 — 클라 전역 설정.)
- **Conversation**: 한 대화 세션(메시지 묶음). 하나의 Project에 N:1로 귀속(미분류 허용).

### `GET /api/v1/projects`
본인 소유 프로젝트 목록.
```json
{
  "projects": [
    { "id": "srv-proj-01", "name": "사내포털 리뉴얼",
      "created_utc": "2026-06-01T00:00:00Z", "updated_utc": "2026-06-26T10:00:00Z",
      "conversation_count": 7 }
  ]
}
```

### `POST /api/v1/projects`  (생성/업서트)
요청:
```json
{ "client_id": "9f2c...(클라 로컬 GUID)", "name": "사내포털 리뉴얼" }
```
응답 201:
```json
{ "id": "srv-proj-01", "name": "사내포털 리뉴얼",
  "created_utc": "...", "updated_utc": "..." }
```
| 요청 필드 | 타입 | 필수 | 설명 |
|------|------|:---:|------|
| `client_id` | string | ✅ | 클라 로컬 프로젝트 GUID(중복 생성 방지·매핑용) |
| `name` | string | ✅ | 프로젝트명 |

> `client_id` 재전송 시 같은 서버 `id` 반환(업서트). 클라는 받은 `id`를 `remote_id`로 로컬에 저장.

### `GET /api/v1/projects/{id}` — 단건(대화 요약 포함, 선택)
```json
{ "id": "srv-proj-01", "name": "사내포털 리뉴얼",
  "conversations": [
    { "id": "srv-conv-11", "client_id": "a1b2...", "title": "로그인 버그 분석",
      "updated_utc": "...", "message_count": 24 }
  ] }
```

### `POST /api/v1/projects/{id}/conversations`  (대화 업서트 — push)
요청(클라 → 서버, 대화 1건 전체):
```json
{
  "client_id": "a1b2...(클라 세션 GUID)",
  "title": "로그인 버그 분석",
  "created_utc": "...", "updated_utc": "...",
  "messages": [
    { "role": "user", "content": "..." },
    { "role": "assistant", "content": "...", "tool_calls": [ ... ] },
    { "role": "tool", "content": "..." }
  ]
}
```
응답 200/201:
```json
{ "id": "srv-conv-11", "client_id": "a1b2...", "updated_utc": "..." }
```

| 요청 필드 | 타입 | 필수 | 설명 |
|------|------|:---:|------|
| `client_id` | string | ✅ | 클라 세션 GUID(업서트 키) |
| `title` | string | ✅ | 대화 제목 |
| `created_utc`/`updated_utc` | string(ISO-8601) | ✅ | 충돌 해소(최신 우선)용 |
| `messages` | array | ✅ | 메시지 배열. 형식은 기존 `/api/v1/agent/chat`의 `messages`와 **동일 스키마** 재사용(role/content/tool_calls) |

> `messages`의 내부 구조(role·content·tool_calls·tool 결과)는 **기존 채팅 요청 계약을 그대로 재사용**하면 됨 — 신규 스키마 불필요.

### (선택) `DELETE /api/v1/projects/{id}` / `DELETE /.../conversations/{id}`
로컬 삭제를 서버에 전파할 경우. 미구현 시 클라는 로컬만 삭제.

### 충돌/동기화 규칙(권장)
- 업서트 키: `client_id`(클라 GUID). 서버는 `client_id ↔ server id` 매핑 유지.
- 충돌 해소: `updated_utc` 최신값 우선(last-write-wins). 양방향이면 더 최신 쪽 채택.
- 클라는 push(로컬→서버) 위주. pull(서버→로컬)은 목록 동기화 수준(다른 PC에서 만든 프로젝트 표시).

---

## C. 우선순위 / 단계 제안
1. **1순위(필수)**: `GET /api/v1/users/me` — 프로필 표시. 이것만 있어도 요구 #5 완성.
2. **2순위(선택)**: `GET/POST /api/v1/projects`, `POST /.../conversations` — 프로젝트 서버 동기화. 없으면 클라는 로컬 전용으로 정상 동작.

## D. 클라이언트 측 DTO 매핑(참고)
| 서버 필드 | 클라 모델 |
|------|------|
| `users/me` 응답 | `Models/UserProfile.cs` (username/display_name/organization/email) |
| `projects[]` | `Models/ProjectRecord.cs` (id↔remote_id, name, *_utc, conversation_count↔로컬 산출) |
| `conversations[]` | `Models/ChatSessionRecord.cs` (client_id↔Id, title, messages, project_id) |

> 메시지(`messages`) 스키마는 기존 `POST /api/v1/agent/chat` 요청의 `messages`와 동일하게 유지해 주세요. 별도 변환 없이 재사용합니다.
