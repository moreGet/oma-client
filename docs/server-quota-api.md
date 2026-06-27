# 서버 API — 본인 토큰 쿼터 조회

> 서버에 **구현 완료**(`quota_handler.go`, `GET /api/v1/me/quota`). 본 문서는 클라이언트 연동 기준 계약 기록.
> 관련: 프로필/동기화 `server-api-spec.md`, 버전 점검 `server-version-api.md`, 도구 정책 `server-tool-policy-api.md`.

## `GET /api/v1/me/quota`
로그인한 사용자 본인의 일·주·월 토큰 한도/사용량/잔여 반환. 클라의 "남은 사용량" 표시에 사용.

- 인증: `Authorization: Bearer <JWT>` (필수, 최소 역할 `user`)
- 에러 envelope: flat `{ "code": "...", "message": "..." }` (관리 API 계약)
- Content-Type: `application/json; charset=utf-8`

### 응답 200
```json
{
  "windows": [
    { "window": "day",   "period": "2026-06-27", "limit": 1000, "used": 250,
      "remaining": 750,  "unlimited": false, "percent_used": 25.0, "percent_remaining": 75.0 },
    { "window": "week",  "period": "2026-W26",   "limit": 5000, "used": 1200,
      "remaining": 3800, "unlimited": false, "percent_used": 24.0, "percent_remaining": 76.0 },
    { "window": "month", "period": "2026-06",    "limit": 0,    "used": 8400,
      "remaining": 0,    "unlimited": true,  "percent_used": 0.0,  "percent_remaining": 100.0 }
  ]
}
```
`windows`는 항상 **일 → 주 → 월** 3개 순서.

| 필드 | 타입 | 설명 |
|------|------|------|
| `window` | string | `day` \| `week` \| `month` |
| `period` | string | 현재 기간 키. 일=`YYYY-MM-DD`, 주=`YYYY-Www`(ISO 주차), 월=`YYYY-MM` (UTC 기준) |
| `limit` | int | 적용 한도(토큰). `0` = 무제한 |
| `used` | int | 이번 기간 누적 사용 토큰 |
| `remaining` | int | `max(0, limit - used)`. 무제한이면 0(→ `unlimited`로 구분) |
| `unlimited` | bool | true면 해당 기간 한도 없음(`limit=0`) |
| `percent_used` | float | 사용률 0~100 (소수 1자리). 무제한이면 0 |
| `percent_remaining` | float | `100 - percent_used`. 무제한이면 100 |

### 에러
| 상태 | code | 상황 |
|------|------|------|
| 401 | `UNAUTHORIZED` | 토큰 없음/만료/무효 |
| 500 | `INTERNAL_ERROR` | 서버 오류 |

## 동작 시맨틱 (클라가 알아야 할 것)
- **한도 결정**: 관리자 설정 멤버별 한도(>0) 우선, 없으면 전역 기본. 둘 다 0이면 무제한.
- **카운트 대상**: chat/agent 응답의 `total_tokens`(프롬프트+완성). provider가 usage 미제공 시 서버가 토크나이저로 추정 보정.
- **소프트 시행**: 일/주/월 중 하나라도 `used ≥ limit`이면 다음 `POST /chat`·`/agent/chat`이 **429**(`TOO_MANY_REQUESTS` / agent 계약은 `rate_limited`)로 거부. 즉 `remaining=0`인 윈도우가 하나라도 있으면 차단 상태.
- **자동 리셋**: `period` 키가 바뀌면 사용량 0부터 재시작(별도 리셋 불필요).
- **다중 인스턴스(LB)**: 사용량/한도가 DB 기반이라 어느 서버로 요청해도 동일.

## 클라이언트 UI (구현)
- `percent_remaining`로 게이지를 그리고, 0%에 가까운 윈도우(일/주/월)를 강조 → 어떤 기간 한도에 걸리는지 즉시 인지.
- `unlimited:true`는 "무제한"으로 표기.
- 새로고침: 로그인 직후 + 매 채팅 턴 완료 후(사용량 변동 반영).
- 서버 미응답/오류 → 쿼터 UI 숨김(graceful).

> 참고: 관리자용(전 멤버 한도/사용량 조회·설정)은 어드민 웹 `/admin/members`. 본 API는 본인 것만 반환.
