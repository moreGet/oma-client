# 설계 — 서버 제어형 위험명령 차단 + 사용 가능 도구 목록

두 가지를 **서버에서 제어 가능**하게 확장하는 설계. 둘 다 공통 원칙을 따른다.

## 공통 원칙 — "2중 안전" (클라 디폴트 = 바닥, 서버 = 추가 제약)

```
최종 적용값 = 클라 내장 디폴트  ∪  서버에서 받은 값
```

- **클라 디폴트는 항상 적용**된다(서버가 제거 불가). 서버는 **더 조일 수만** 있고 느슨하게 못 한다.
- 서버 값이 없으면(미구현/오프라인/null) → **클라 디폴트만**으로 정상 동작(graceful).
- 서버 값은 **로그인 시 1회 받아 세션 캐시**(도구 정책과 동일 라이프사이클). 변경은 재로그인 반영.

> 이 방향이면 서버가 죽거나 잘못된 값을 줘도 **보안이 약해지지 않는다**(디폴트가 바닥을 지킴).

---

## #1. 위험 명령 패턴 차단 — 디폴트 + 서버 병합

### 현재
`SecurityValidator`(static)에 정규식 블랙리스트(`CommonBlacklist`/`PowerShellBlacklist`/`BlockedPaths`)가 하드코딩.
`RunCommandTool` 실행 전 `SecurityValidator.Validate(script, type)` 호출.

### 설계
- `SecurityValidator`를 **디폴트 + 서버 추가 패턴**을 병합 검사하도록 확장.
- 검사 순서: ① 클라 디폴트(항상) → ② 서버 추가 패턴(명령) → ③ 서버 추가 경로 → 하나라도 매치되면 차단.
- 서버 패턴 로드/캐시는 `ToolPolicyService`와 같은 시점(로그인). 별도 인터페이스 없이 `SecurityValidator` 내부 **static volatile 필드**(`_serverPatterns`/`_serverPaths`)에 서버값을 직접 보관하고, 로그인 시 `SecurityValidator.SetServerPatterns(policy)`로 주입(스냅샷 통째 교체 → 스레드 안전). 서버값이 없으면 `SetServerPatterns(null)`로 비워 디폴트만 적용.

### 안전 장치(서버 정규식 수용 시)
- 서버 정규식은 **컴파일 시 검증** + **매치 타임아웃**(예: 100ms) 적용 → 잘못된/악성 정규식(ReDoS) 방어. 컴파일 실패 패턴은 **무시(skip)**.
- 또는 더 안전하게 `type: "substring" | "regex"`를 받아 substring은 단순 포함 검사(정규식 위험 회피).

### 서버 API
**`GET /api/v1/security/command-policy`** (Bearer, user)
```json
{
  "blocked_patterns": [
    { "type": "regex",     "pattern": "\\bnet\\s+user\\b", "reason": "사용자 계정 조작 금지", "script_type": "any" },
    { "type": "substring", "pattern": "bcdedit",            "reason": "부트 설정 변조 금지",   "script_type": "powershell" }
  ],
  "blocked_paths": [
    { "type": "substring", "pattern": "D:\\\\sensitive", "reason": "민감 디렉토리 접근 금지" }
  ]
}
```
| 필드 | 값 | 설명 |
|------|----|------|
| `type` | `regex` \| `substring` | 매칭 방식. 생략 시 `substring`(안전 기본) |
| `pattern` | string | 패턴 |
| `reason` | string | 차단 사유(사용자/로그 표시) |
| `script_type` | `any` \| `powershell` \| `cmd` | 적용 셸. 생략 시 `any` |

- **클라 동작**: 디폴트 블랙리스트 ∪ 위 패턴으로 검사. 미구현(404)/오프라인 → 디폴트만.
- 서버는 **추가만** 가능(디폴트 패턴을 끄는 필드는 두지 않는다 — 2중 안전 원칙).

---

## #2. 사용 가능한 도구 목록 — 서버가 노출/허용 통제

### 현재
- `ToolRegistry.ToSchemas()`가 클라 내장 도구 **전부**를 모델에 노출.
- `ToolPolicyService`(`GET /api/v1/tools/policy`의 `enabled`/`disabled`)는 **실행만** 게이팅하고 **노출은 안 거름** → 비활성 도구도 모델이 보고 호출 시도 → 차단(비효율).

### 설계 (기존 `tools/policy` 재사용 + 노출 필터 추가)
- 도구 *실행 코드*는 계속 **클라에 내장·배포**(네이티브 OS 접근 때문). 서버는 **그중 무엇을 켤지**를 통제한다.
- 핵심 변경: **서버의 사용가능 목록으로 `ToSchemas()`(모델 노출)까지 필터**. 비활성 도구는 모델이 **아예 못 봄** + 실행도 차단.
- 적용 규칙(기존 `tools/policy` 그대로 활용):
  ```
  노출/허용 도구 = 클라 내장 도구  ∩  서버 enabled(있으면)  −  서버 disabled
  ```
  - 서버 `enabled=null` → 클라 내장 전체 노출(현행). `enabled` 지정 시 그 교집합만.
  - 클라에 없는 도구명을 서버가 줘도 무시(네이티브 코드 없으므로). → 새 도구는 **클라 배포 후** 서버가 켜는 구조.
- 즉 **새 도구 추가 워크플로우**: ① 클라에 도구 구현·배포 → ② 서버 정책에서 해당 도구명을 enabled에 추가 → 즉시(또는 재로그인 시) 사용자에게 노출.

### 클라 구현 포인트
- `AgentOrchestrator.BuildRequest`가 `tools.ToSchemas()`를 `ToolPolicyService.IsExposed(name)`로 거른 뒤 요청에 첨부(`IToolRegistry`는 변경 없음). 실행 게이트(`EvaluateAsync`)는 기존 유지 → **노출·실행 동일 규칙**(cached: disabled 우선 → enabled 화이트리스트).

### 서버 API
**기존 `GET /api/v1/tools/policy` 재사용**(신규 API 불필요):
```json
{ "mode": "cached", "enabled": ["read_file","write_file","grep","run_command"], "disabled": ["screenshot"] }
```
→ 이 `enabled`/`disabled`가 이제 **노출+실행 둘 다** 통제.

### (선택) 더 풍부한 제어가 필요하면 — 도구 카탈로그
서버가 도구별 **메타데이터 override**(표시명/설명/위험도/카테고리/정렬)까지 주고 싶을 때:
**`GET /api/v1/tools/catalog`** (Bearer, user)
```json
{
  "tools": [
    { "name": "run_command", "available": true,  "risk": "execute",
      "description_override": "사내 정책상 화이트리스트 명령만 실행", "category": "shell", "order": 10 }
  ]
}
```
- `available=false` → 노출·실행 제외. `risk`/`description_override` → 클라 표시·게이트에 반영(없으면 클라 내장값).
- **권장**: 1차는 기존 `tools/policy`로 충분. 카탈로그는 메타데이터 통제가 실제 필요할 때 도입.

---

## 서버에서 구현해야 할 API 목록 (요약)

| 우선 | Method · Path | 용도 | 비고 |
|:---:|---|---|---|
| **신규** | `GET /api/v1/security/command-policy` | 추가 위험명령/경로 차단 패턴 | #1. 미구현 시 클라 디폴트만 |
| 기존확장 | `GET /api/v1/tools/policy` | 사용 가능 도구(enabled/disabled) | #2. 클라가 **노출까지** 필터하도록 동작 확대(서버 변경 없음, 클라 구현) |
| (선택) | `GET /api/v1/tools/catalog` | 도구별 메타 override(표시명/위험도/정렬) | #2 확장. 필요 시 |

공통: Bearer(user), 중첩 envelope, **로그인 시 1회 로드·세션 캐시**, 미구현/오프라인 시 graceful(클라 디폴트).

---

## 클라이언트 작업 범위(구현 완료)
1. `SecurityValidator` 내부 **static 상태**(`_serverPatterns`/`_serverPaths` + `SetServerPatterns()`)로 서버 패턴을 보관하고, 디폴트∪서버 병합 검사로 확장(정규식 타임아웃·컴파일 검증 포함). — *별도 `ICommandSecurityPolicy` 인터페이스는 두지 않고 단순 static 방식 채택.*
2. `AgentApiClient`: `GetCommandSecurityPolicyAsync()` 추가(graceful).
3. `ToolPolicyService`/오케스트레이터: `ToSchemas()`를 정책(`IsExposed`)으로 필터 → 노출·실행 일관.
4. 로그인 라이프사이클에 보안정책 로드 추가(`ToolPolicyService.LoadAsync`와 동일 시점, `AgentSessionViewModel`).
5. 문서: 명령 보안 설계는 **본 문서(`server-controlled-security-and-tools.md`)로 분리**해 관리(`server-tool-policy-api.md`엔 미통합).

> 모두 기존 패턴(graceful·로그인 캐시·2중 안전)을 따르므로 리스크 낮음. 서버 미구현이어도 현행 동작 그대로.
