# 서버 API 요구 스펙 — 클라이언트 버전 점검

대상: 서버(API) 팀. 클라이언트(OhMyAgent.AiAgent.Client)가 자기 버전을 점검하고 업데이트 알림을 띄우기 위해 호출하는 엔드포인트.
인증: `Authorization: Bearer <JWT>`. JSON UTF-8.
원칙: **서버 미구현/오류 시 클라이언트는 graceful**(알림만 생략, 앱은 정상 동작). 하드 차단은 하지 않음(알림 기반).

> 관련 문서: 프로필·프로젝트 동기화는 `docs/server-api-spec.md` 참고. 본 문서는 버전 점검만 다룹니다.

---

## 배경 — 클라이언트가 이미 보내는 버전 정보
클라이언트는 모든 채팅 요청의 `metadata.client_version`에 자기 SemVer를 실어 보냅니다(예: `"1.3.0"`).
출처: `.csproj <Version>` → 어셈블리 버전 → `AppVersion.Semantic`. 서버는 이 값으로 클라 버전을 이미 식별할 수 있습니다.

---

## 엔드포인트

### `GET /api/v1/client/version`
현재 배포된 클라이언트의 최신/최소지원 버전 정보를 반환. 클라이언트는 연결·인증 직후 1회 호출해 자기 버전과 비교한다.

**응답 200**
```json
{
  "latest": "1.4.0",
  "minimum_supported": "1.2.0",
  "download_url": "https://intra.example.com/oma-client/latest",
  "notice": "보안 패치 포함. 업데이트를 권장합니다.",
  "mandatory": false
}
```

| 필드 | 타입 | 필수 | 설명 |
|------|------|:---:|------|
| `latest` | string (SemVer) | ✅ | 현재 배포된 최신 버전. 클라가 이보다 낮으면 "새 버전 사용 가능" 알림 |
| `minimum_supported` | string (SemVer) | ✅ | 서버가 허용하는 최소 클라 버전. 클라가 이보다 낮으면 "필수 업데이트" 알림 |
| `download_url` | string\|null | ⛔ | 사내 배포 위치(설치 파일/페이지). 알림에서 안내 |
| `notice` | string\|null | ⛔ | 사용자에게 보여줄 추가 안내(릴리스 요지 등) |
| `mandatory` | bool | ⛔ | true면 강한 업데이트 권고(클라는 경고색 배너로 표시). 기본 false |

**에러/부재**: `401`(토큰 무효) · `404`/`501`(미구현) · 네트워크 오류 → 클라는 **알림 생략**(앱 정상). 

---

## 클라이언트 동작 (구현 완료분)
연결+인증 직후 `GetClientVersionAsync()` 호출 → `System.Version`으로 비교:

| 조건 | 클라 동작 |
|------|----------|
| `current < minimum_supported` | 경고색 배너 "필수 업데이트 필요" (`UpdateMandatory=true`) |
| `minimum_supported ≤ current < latest` | 액센트 배너 "새 버전 N 사용 가능" |
| `current ≥ latest` | 알림 없음(최신) |

- 비교 실패(파싱 불가)나 응답 없음 → 알림 없음.
- **하드 차단은 클라가 하지 않음.** 정책상 강제 차단이 필요하면 서버가 보호된 엔드포인트에서 구버전 토큰을 거부(예: `426 Upgrade Required`)하는 방식을 권장 — 그 경우 명세는 별도 협의.

---

## 버전 문자열 규약
- SemVer `MAJOR.MINOR.PATCH` (예: `1.3.0`). 클라가 보내는 `client_version`도 동일 형식(빌드 메타 `+hash`는 표시용이며 비교엔 SemVer 코어만 사용).
- 비교는 `latest`/`minimum_supported`/`client_version` 모두 SemVer 코어 기준.

## 우선순위
- 선택 기능. 프로필(`/users/me`)·프로젝트 동기화와 독립. 미구현이어도 클라 정상 동작하므로 **여유 있을 때 구현**해도 무방.

## 향후(선택) — 폐쇄망 보안 업데이트
클라엔 이미 코드서명 검증(`IAuthenticodeVerifier`)·무결성 점검(`IntegrityWindow`) 인프라가 있습니다. `download_url`로 받은 배포물을 **서명/해시 검증 후 적용**하는 자동 업데이트는 이 인프라 위에 확장 가능(별도 설계).
