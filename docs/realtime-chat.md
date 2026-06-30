# 실시간 채팅(메신저) — 사용자 간 메시징

> **LLM 채팅(`/agent/chat`)과 완전히 별개**인 **사람↔사람** 실시간 메신저입니다.
> 서버의 `/api/v1/chat/*` REST + `/api/v1/chat/ws` WebSocket 과 통신하며, 단체(group)·1:1(direct) 방,
> 메시지 송수신·수정·삭제, 읽음·안읽음, 타이핑, 온라인 상태(presence), 멘션, 첨부, 멤버 관리를 실시간 반영합니다.
> 이 문서의 모든 계약은 **라이브 서버(127.0.0.1:8080) 전수 검증(REST 32 + WS 10 = 42/42 PASS)** 으로 확인되었습니다.

---

## 1. 사용자 경험

- **진입**: 시스템 트레이 메뉴 "메신저" 또는 메인 창 사이드바 "메신저" 버튼(안읽음 Pill 배지). 별도 `ChatMessengerWindow`(프레임리스, 좌측 방 목록 / 우측 대화방)로 열립니다.
- **방 목록**: 최근 활동순 정렬, 안읽음 배지, 이니셜 아바타, 마지막 미리보기·시각. "새 대화"로 1:1/단체 방 생성.
- **대화방**: 좌/우 말풍선, 무한 스크롤 이력 로드, 타이핑 인디케이터, 읽음 표시, 멘션 하이라이트·자동완성, 첨부 칩, 컴포저(Enter 전송 / Shift+Enter 줄바꿈).
- **수정/삭제**: 본인 메시지 우클릭 → 수정/삭제. 삭제는 "삭제된 메시지" 자리표시.
- **멤버 관리**(단체 방 한정): 멤버 추가 / 강퇴(생성자만) / 나가기.
- **전역 안읽음 배지**: 트레이·사이드바·메신저 타이틀바에 실시간 동기화.
- **연결 상태 점**: Connected(녹) / Connecting·Reconnecting(주황) / Disconnected(적). 끊기면 지수 backoff 자동 재연결 + 이력 재동기화.

---

## 2. 클라이언트 구조 (MVVM, 3계층 서비스)

```
Views/Chat/                         ViewModels/Chat/                 Services/Chat/
  ChatMessengerWindow                 ChatMessengerViewModel(셸)        IChatRealtimeService ← VM 단일 의존(파사드)
  ├ ChatRoomsView                     ├ ChatRoomsViewModel               ├ IChatApiClient   (REST 전부)
  ├ ChatRoomView                      │  └ ChatRoomListItemViewModel      ├ IChatSocketClient(ClientWebSocket)
  │  ├ Controls/ChatMessageBubble     ├ ChatRoomViewModel                └ IChatMessengerCoordinator(창 토글)
  │  └ Controls/MentionAutoComplete   │  ├ ChatMessageViewModel
  ├ RoomMembersView                   │  ├ RoomMembersViewModel         Models/Chat/
  └ MentionFeedView                   │  └ MentionAutoCompleteViewModel   ChatDtos / ChatWireEnvelopes(+ChatJson) / ChatEnums
                                      └ MentionFeedViewModel             JwtIdentity(식별자), ChatApiException
```

- **`IChatApiClient`** — REST 호출. 기존 `AgentApiClient` 패턴 미러(자체 `ApplyAuth`/에러 변환, `ChatJson.Options`). `ChatApiException(StatusCode, Code, Message)` 로 상태코드를 보존해 호출자가 401 분기.
- **`IChatSocketClient`** — BCL `System.Net.WebSockets.ClientWebSocket`(추가 패키지 없음). `Authorization: Bearer` 헤더로 핸드셰이크, 백그라운드 수신 펌프(멀티프레임 누적 → `type` 디스패치 → C# `event` 발화), **지수 backoff 자동 재연결**(1→2→4…최대 30s + 지터), `KeepAliveInterval` ping-pong.
- **`IChatRealtimeService`** — REST+WS 파사드. 방/메시지 상태 보유, **에코 dedup(message id)**, **읽음 단조성(전진만)**, 안읽음 집계, **재연결 후 재동기화**(이력+unread+reads 병합). 모든 상태 변경 발화는 `UiDispatch.InvokeAsync` 로 UI 스레드 마샬.
- **컴포지션 루트**: `App.OnStartup` 에서 수동 조립(외부 DI 없음). 메신저 최초 표시 시 1회 `StartAsync`(WS connect + unread). `ReturnToLogin`/종료 시 `StopAsync` + Dispose.

---

## 3. 서버 계약 (검증됨)

공통: Base `/api/v1`, `Authorization: Bearer <JWT>`, **중첩 에러 envelope** `{ "error": { "code", "message" } }`, **HTTP 상태로 분기**. 모든 시각 = **unix epoch 초(정수)**.

### REST
| 메서드·경로 | 용도 |
|---|---|
| `GET /chat/rooms` | 방 목록(안읽음 포함, 최근활동순) |
| `POST /chat/rooms` | 단체 방 생성 `{name, member_ids[]}`(생성자 자동 포함) |
| `POST /chat/rooms/direct` | 1:1 방 가져오기/생성 `{user_id}`(중복 방지, 자기자신 400) |
| `GET /chat/rooms/{id}/messages?limit=&before=` | 메시지 이력(최신순, 페이지네이션) |
| `POST /chat/rooms/{id}/messages` | REST 전송 `{content, mentions?, attachments?}`(둘 다 없으면 400) |
| `PATCH /chat/rooms/{id}/messages/{mid}` | 본인 메시지 수정(타인 403, 삭제됨 404) |
| `DELETE /chat/rooms/{id}/messages/{mid}` | 본인 메시지 소프트 삭제(멱등) |
| `POST /chat/rooms/{id}/read` | 읽음 처리(단조 증가) |
| `GET /chat/rooms/{id}/reads` | 멤버별 읽음 위치 |
| `GET /chat/unread` | 총/방별 안읽음 배지 |
| `GET /chat/rooms/{id}/members` · `POST` · `DELETE .../{mid}` · `POST .../leave` | 멤버 목록/추가/강퇴/나가기(추가·강퇴·나가기는 **group 한정**, 강퇴는 생성자만) |
| `GET /chat/rooms/{id}/presence` | 온라인 멤버 |
| `GET /chat/mentions?limit=` | 나를 멘션한 메시지 피드 |
| `POST /chat/attachments` | 첨부 업로드(multipart, 파트명 `file`, ≤10MiB) → `{id,file_name,content_type,size_bytes,url}` |
| `GET /chat/attachments/{aid}` | 첨부 다운로드(바이너리) |

### WebSocket `GET /chat/ws`
- **클라→서버**: `{"type":"send","room_id","content","mentions?","attachments?"}` · `{"type":"typing","room_id","state":"start|stop"}`
- **서버→클라**: `message` / `message_edited` / `message_deleted`(`message:{…}`) · `read`(`read:{room_id,member_id,last_read_at}`) · `typing`(`typing:{…}`, 발신자 제외) · `member_joined`/`member_left`(`member:{…}`) · `presence`(`presence:{member_id,online}`) · `error`(`error:"…"`, **그 연결로만**)

---

## 4. 식별자 (중요)

- **현재 사용자 식별자 = JWT `sub` 클레임(member UUID)**. 채팅의 `sender_id`/`member_id`/direct의 `user_id` 가 모두 이 member UUID 입니다(**username 아님**).
- `GET /users/me` 응답에는 id 필드가 없으므로, 클라이언트는 저장된 토큰을 디코드해 식별합니다 — `Services/Chat/JwtIdentity.MemberId(token)`. 이 값을 `ChatMessengerViewModel.currentUserId` 와 `ChatRealtimeService.MyId()` 가 **동일 소스**로 사용합니다.
- "새 대화"에서 상대를 지정할 때도 **member UUID** 기준입니다(전체 사용자 디렉터리 API가 별도로 없어, 현재는 UUID 직접 입력).

---

## 5. 엣지 케이스 / 상태별 처리

- **에코 dedup**: 본인이 보낸 메시지도 `message` 이벤트로 되돌아옵니다(다기기 일관성) → message **id로 dedup**.
- **읽음 단조성**: `last_read_at` 은 전진만(뒤로 가지 않음). **안읽음 정의** = 내 `last_read_at` 이후 **남이 보낸**(삭제 제외) 메시지 수.
- **REST 이력의 한계**: `GET …/messages` 응답에는 **`mentions`/`attachments` 가 빠집니다**(WS `message` 이벤트 DTO 에만 포함). 따라서 이력으로 들어온 메시지는 멘션/첨부가 표시되지 않고, **실시간 수신분만** 렌더됩니다. (서버 보완 전까지의 임시 대응이며 코드 주석에 명시.)
- **상태별 에러**: **401에서만** 재로그인(세션 폐기). 403/404/429/5xx 는 메시지만 표시하고 **로그아웃하지 않음**.
- **비멤버 WS send**: 서버가 `{"type":"error","error":"FORBIDDEN: not a room member"}` 를 그 연결로만 보낸 뒤 **연결을 종료**합니다 → 클라는 자동 재연결로 복구(멤버 방으로만 send 하면 미발생).
- **presence**는 서버 메모리(허브) 전용으로 영속되지 않습니다(재기동 시 사라짐).

---

## 5.1 이름 표시(디렉터리)
- 채팅 식별자는 member UUID 라(§4), UI 는 **이름 해석 캐시**(`IChatRealtimeService.DisplayName(id)`)로 사람 이름을 표시한다: 멤버 목록·1:1 방 헤더/목록·멘션 후보·멘션 피드 발신자·아바타 이니셜.
- 캐시 소스: 본인 `GET /users/me`(display_name/username) + best-effort `GET /members`. **`/members` 는 admin 전용**이라 admin 은 모든 멤버 username 이 보이고, 일반 user 는 본인만 이름·나머지는 **UUID 앞 8자리 폴백**.
- 캐시 갱신 시 `DirectoryUpdated` 이벤트로 라벨 일괄 재해석.
- 서버가 **방 멤버 스코프 이름 엔드포인트**([`docs/server-request-chat-directory.md`](server-request-chat-directory.md))를 추가하면 일반 user 도 즉시 이름 표시(소스 한 줄 교체).

## 6. 알려진 한계 / 후속 과제

- 일반 user 의 타 멤버 **이름 표시**는 서버의 멤버 스코프 이름 API 추가 전까지 UUID 앞자리 폴백(위 §5.1, 서버 요청서 제출됨).
- 전체 사용자 디렉터리 API 부재 → 새 방 생성·멤버 추가 시 상대 **member UUID 직접 입력**(자동완성/검색 UI는 디렉터리 API 추가 후).
- REST 이력의 mentions/attachments 복원은 **서버 측 DTO 보완** 필요.
- 첨부 인라인 미리보기(썸네일), 이모지 리액션, 메시지 검색은 범위 외(서버 미제공).
- 재로그인으로 토큰이 바뀌어도 메신저 VM의 `currentUserId` 는 즉시 갱신되지 않습니다(서버 `sender_id` 가 권위라 표시 영향은 최소 — 다음 메신저 재오픈 시 정정).
