# 서버 요청 — 채팅 멤버 이름 조회 (member directory)

> 대상: OhMyAgent.AiAgent.Server 팀
> 작성: 클라이언트(OhMyAgent.AiAgent.Client) — 실시간 채팅(메신저) 기능
> 우선순위: **High** (현재 일반 사용자 화면에서 사람 이름이 전부 UUID 로 보임)

## 문제

채팅의 모든 사람 식별자(`sender_id` / `member_id` / direct `user_id`)는 **member UUID** 다(예: `4bbd47a2-4837-42a0-abda-3d2efc91a642`).
클라이언트가 이 UUID 를 **사람이 읽을 수 있는 이름(username / display_name)** 으로 바꿀 방법이 일반 사용자에겐 없다:

- `GET /api/v1/users/me` → 본인 이름만(다른 멤버 불가).
- `GET /api/v1/members` → **admin 전용**. 일반 `user` 역할은 **403**(라이브 확인됨).
- `GET /api/v1/chat/rooms/{id}/members` → `{members:[<uuid>...]}` — **UUID 만, 이름 없음**.

결과: 일반 사용자 화면에서 **멤버 목록·멘션 발신자·1:1 방 상대·"멤버 추가" 입력**이 전부 UUID 로 표시/입력된다.
(admin 은 `/members` 로 username 을 끌어와 부분적으로 이름 표시가 되지만, 일반 사용자는 불가.)

## 요청 (둘 중 택1 — A 선호)

### A) 기존 방 멤버 엔드포인트에 이름 포함 (backward-compatible 확장) — **권장**
`GET /api/v1/chat/rooms/{id}/members` 응답을 멤버 메타 포함 형태로 확장. **방 멤버라면 누구나**(현재와 동일한 멤버십 스코프) 접근.

```jsonc
// 현재
{ "members": ["<uuid>", "<uuid>"] }

// 요청 (확장) — 택1
{ "members": [
    { "id": "<uuid>", "username": "probe2", "display_name": "홍길동" },
    { "id": "<uuid>", "username": "admin",  "display_name": "신성현" }
] }
```
- 하위호환이 필요하면 `?detail=1` 쿼리로 풍부한 형태를 반환하고, 무인자는 기존 배열 유지해도 됨.
- `display_name` 이 비면 클라가 `username` 으로 폴백한다(둘 중 하나는 반드시 채워주면 됨).

### B) 멤버십 스코프 배치 이름 조회 엔드포인트 (대안)
`GET /api/v1/chat/members?ids=<uuid>,<uuid>,...` — **요청자와 같은 방에 속한 멤버에 한해** 이름 반환.
```jsonc
{ "members": [ { "id": "<uuid>", "username": "probe2", "display_name": "홍길동" } ] }
```
- 인가: 요청자와 **공유 방이 있는** 멤버만(타인 디렉터리 무단 열람 방지).

## 인가 / 보안
- 핵심: **admin 권한 없이**, "내가 속한 방의 멤버 이름"만 볼 수 있으면 된다. 전체 사용자 디렉터리 노출은 불필요(원치 않음).
- 비멤버/비공유 멤버 id 는 응답에서 제외(또는 404). 기존 chat 멤버십 게이트 재사용 가능.

## 클라이언트 소비 방식 (이미 준비됨)
- 클라에 **이름 해석 캐시(`memberId → 표시이름`)** 가 있고, 현재는 `/users/me`(본인) + `/members`(admin 한정)만 채운다.
- 위 A/B 가 생기면 **소스 한 줄 교체**로 일반 사용자도 방 멤버 이름이 즉시 표시된다(멤버 목록/멘션/1:1 상대/아바타 이니셜).
- 시각 등 다른 계약 변경은 불필요. epoch/중첩 envelope 등 기존 규약 그대로.

## 참고
- 관련 클라 문서: [`docs/realtime-chat.md`](realtime-chat.md) §4(식별자).
- 현재 클라 임시 대응: admin 은 `/members` username, 그 외는 **UUID 앞 8자리** 폴백 표시.
