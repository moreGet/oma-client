# 서버 API 요구 스펙 — 도구 정책 게이트

대상: 서버(API) 팀. 클라이언트가 **어떤 도구를 실행해도 되는지**를 서버 정책으로 통제하기 위한 엔드포인트.
인증: `Authorization: Bearer <JWT>`. JSON UTF-8.
원칙: 서버 미구현/오류 시 클라이언트는 graceful — **정책 없음 = 전체 허용**(로컬 권한 게이트·샌드박스가 여전히 방어). 앱은 정상 동작.

> 관련: 버전 점검은 `docs/server-version-api.md`, 프로필/동기화는 `docs/server-api-spec.md`.

---

## 개념 — 도구 실행 결정 흐름

```
모델(LLM)이 도구 호출 결정
   ↓
① 서버 도구 정책 게이트   ← 본 문서 (cached/realtime)
   ↓
② 로컬 권한 게이트        ← 위험도(ToolRisk)+모드(수동/안전자동/전체자동), 사용자 승인 (이미 구현됨)
   ↓
③ 샌드박스                ← 작업 디렉토리 밖 경로 차단 (이미 구현됨)
   ↓
클라에서 실제 실행(ExecuteAsync)
```

서버 정책은 ①에 해당. ②③은 클라 기존 방어.

## 두 가지 모드 (서버가 결정)

| 모드 | 동작 | 변경 반영 시점 |
|------|------|------|
| **cached** | 로그인 시 받은 허용/차단 목록을 클라가 로컬 적용(왕복 없음) | **재로그인** 시 |
| **realtime** | 도구 실행 직전마다 서버에 인가 질의 | **즉시**(매 호출) |

> **중요**: 모드와 (cached) 목록은 **로그인 시 1회** 받아 세션 동안 캐시된다. 서버에서 `cached → realtime`으로 바꿔도 **해당 사용자가 재로그인해야** 반영된다(재연결만으로는 반영 안 됨). 이는 의도된 동작이다.

---

## 엔드포인트

### 1) `GET /api/v1/tools/policy`  — 로그인 시 1회 호출
세션의 도구 정책(모드 + cached 목록)을 반환.

**응답 200**
```json
{
  "mode": "cached",
  "enabled": ["read_file", "write_file", "list_directory", "glob", "grep"],
  "disabled": ["run_command", "kill_process"]
}
```

| 필드 | 타입 | 필수 | 설명 |
|------|------|:---:|------|
| `mode` | string | ✅ | `"cached"` 또는 `"realtime"`. 그 외 값은 클라가 `cached`로 간주 |
| `enabled` | string[]\|null | ⛔ | (cached) 허용 도구명 화이트리스트. **null/생략 = 전체 허용** |
| `disabled` | string[]\|null | ⛔ | (cached) 차단 도구명 블랙리스트. **`disabled`가 `enabled`보다 우선** |

**cached 판정 규칙(클라 구현)**:
1. 도구명이 `disabled`에 있으면 → **차단**
2. `enabled`가 지정(비-null)됐는데 도구명이 없으면 → **차단**
3. 그 외 → **허용**

`realtime` 모드면 `enabled`/`disabled`는 무시(아래 2번 엔드포인트로 매번 질의).

**부재/오류**: `404`/`501`/오프라인 → 클라는 정책 없음으로 간주(**전체 허용**, graceful).

### 2) `POST /api/v1/tools/authorize`  — realtime 모드에서만, 도구 실행 직전 매번
특정 도구 1회 실행을 인가.

**요청**
```json
{
  "tool": "write_file",
  "arguments": { "path": "안녕.txt", "content": "안녕" }
}
```
| 필드 | 타입 | 필수 | 설명 |
|------|------|:---:|------|
| `tool` | string | ✅ | 도구명 |
| `arguments` | object | ⛔ | 모델이 넘긴 인자(정책 판단에 활용 가능 — 예: 경로·명령 검사) |

**응답 200**
```json
{ "allowed": true, "reason": null }
```
| 필드 | 타입 | 필수 | 설명 |
|------|------|:---:|------|
| `allowed` | bool | ✅ | 실행 허용 여부 |
| `reason` | string\|null | ⛔ | 거부 사유(거부 시 모델·로그에 표시) |

**오류/응답 없음(realtime)**: 클라는 **차단(fail-closed)** — 실시간 정책이 활성 상태이므로 안전 우선. 사유 "정책 서버 응답 없음".

---

## 클라이언트 동작 요약 (구현 완료분)

| 상황 | 결과 |
|------|------|
| 정책 엔드포인트 없음/오프라인(미로드) | 전체 허용(fail-open) — 로컬 게이트·샌드박스가 방어 |
| cached · disabled 포함 | 차단 |
| cached · enabled 지정인데 미포함 | 차단 |
| cached · 그 외 | 허용 |
| realtime · authorize allowed=true | 허용 |
| realtime · allowed=false | 차단(사유 표시) |
| realtime · 응답 없음 | 차단(fail-closed) |

- 차단된 도구는 **로컬 승인 카드·실행을 건너뛰고**, 모델에 "서버 정책에 의해 차단됨: <사유>"를 도구 결과(오류)로 피드백 → 모델이 다른 방법을 모색.
- 정책(모드 포함)은 **로그인/재로그인 시에만** 로드. 세션 중 재연결로는 갱신 안 됨.

## 운영 권장
- **cached**가 기본(성능·오프라인 내성). 고위험·강감사 사용자/조직만 **realtime**.
- realtime에서 `authorize`는 매 도구 호출마다 오므로 **낮은 지연**으로 응답할 것(에이전트 루프 체감 속도 직결).
- 도구명은 클라 내장 도구 식별자와 일치해야 함(예: `read_file`, `write_file`, `edit_file`, `list_directory`, `glob`, `grep`, `create_directory`, `move`, `copy`, `delete`, `run_command`, `get_environment`, `clipboard_read`, `clipboard_write`, `list_processes`, `list_processes_memory_kb`, `start_process`, `kill_process`, `http_fetch`, `screenshot`).

## 우선순위
- 선택 기능. 미구현이어도 클라 정상 동작(전체 허용). 통제 강화가 필요해질 때 도입.
