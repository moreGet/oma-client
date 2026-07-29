# 서버 API 요구 스펙 — HTTP 압축(gzip)

대상: 서버(API)·인프라 팀. 클라이언트(OhMyAgent.AiAgent.Client)와 서버 사이 전송량을 줄이기 위한 요구사항.
원칙: **서버 미지원 시 클라이언트는 graceful**(압축 없이 그대로 동작). 클라이언트가 먼저 깨지는 변경은 없음.

> 관련 문서: 전송 형식 전반은 `docs/API_CONTRACT.md`. 본 문서는 압축만 다룹니다.

---

## 요약 — 서버가 해줘야 할 것

| # | 항목 | 담당 | 난이도 | 효과 |
|---|------|------|:---:|------|
| **1** | **요청 본문 gzip 수용** (`Content-Encoding: gzip`) | **API 앱** | 중 | **큼** — 채팅 요청 76~80% 감소 |
| 2 | 응답 압축 (`application/json`) | nginx | 하 | 중간 |
| 3 | SSE(`text/event-stream`)는 압축 **제외** | nginx | 하 | 회귀 방지 |

**핵심: 1번은 nginx로 해결되지 않습니다.** 아래 "왜 nginx가 아닌가" 참고.

---

## 배경 — 현재 상태 (클라이언트 코드 기준, 실측 확인됨)

- 요청 본문: `application/json`, UTF-8, **압축 없음**. JSON 자체는 들여쓰기 없는 최소 형식이고 null 필드는 생략됨(`AgentJson.Options`).
- 응답: 채팅은 `text/event-stream`(SSE), 그 외는 JSON.
- 클라이언트는 종전까지 `Accept-Encoding` 헤더를 **아예 보내지 않았음**(핸들러 기본값 `DecompressionMethods.None`).

### 왜 요청 압축의 효과가 큰가

에이전트는 도구를 호출할 때마다 대화 **전문을 다시 보냅니다**(`ContextCompactor.BuildWireMessages`). 한 턴에 도구 왕복이 10~30회면 같은 이력이 그만큼 반복 전송됩니다. 이력에는 도구가 읽은 **소스 코드 원문**이 그대로 들어 있어 gzip이 특히 잘 듣습니다.

**실측** (이 저장소의 실제 `.cs` 파일을 도구 결과로 채운 세션, gzip `Optimal`):

| 세션 크기 | 요청 본문 원본 | gzip 후 | 절감 |
|---|---:|---:|---:|
| 메시지 50개 | 64.2 KB | 15.2 KB | **76.3%** (4.2배) |
| 메시지 200개 | 250.2 KB | 54.9 KB | **78.1%** (4.6배) |
| 메시지 600개 | 650.0 KB | 131.4 KB | **79.8%** (4.9배) |

한 턴에 왕복 15회인 200메시지 세션이면 요청만 **약 3.7 MB → 0.8 MB**로 줄어듭니다.

---

## 1. 요청 본문 gzip 수용 (API 앱 담당)

클라이언트가 큰 본문을 보낼 때 다음 헤더를 붙일 수 있도록 서버가 받아줘야 합니다.

```http
POST /api/v1/agent/chat HTTP/1.1
Content-Type: application/json
Content-Encoding: gzip
Authorization: Bearer <JWT>

<gzip 압축된 JSON 바이트>
```

**요구사항**

| 항목 | 요구 |
|------|------|
| 지원 인코딩 | `gzip` (필수). `deflate`/`br` 는 선택 |
| 적용 대상 | 모든 `POST`/`PUT` JSON 엔드포인트. 특히 `POST /api/v1/agent/chat` |
| 헤더 부재 시 | 종전대로 평문 JSON으로 처리 (하위 호환 필수) |
| 미지원 인코딩 | `415 Unsupported Media Type`, 본문에 `{"error":{"code":"unsupported_encoding"}}` |
| 압축 해제 실패 | `400 Bad Request`, `{"error":{"code":"malformed_body"}}` |
| 해제 후 크기 상한 | **필수** — zip bomb 방어. 권장 상한 예: 해제 후 64 MB 또는 압축비 100:1 초과 시 `413` |

**보안 주의**: 압축 해제는 신뢰할 수 없는 입력을 다루는 지점입니다. 스트리밍 해제 + 누적 바이트 상한을 반드시 두세요. 상한 없이 전량 해제하면 작은 요청으로 서버 메모리를 고갈시킬 수 있습니다.

### 왜 nginx가 아닌가 (중요)

nginx는 **클라이언트가 보낸 요청 본문을 풀어주지 않습니다.** `ngx_http_gunzip_module`은 이름과 달리 *업스트림이 준 응답*을 푸는 모듈이며, 요청 본문용이 아닙니다.

따라서 nginx만 손대면 압축된 바이트가 **그대로 업스트림에 전달**되어 API 앱의 JSON 파싱이 실패합니다. 요청 압축은 **반드시 API 애플리케이션에서** 처리해야 합니다.

- FastAPI/Starlette → 요청 본문 해제 미들웨어 직접 작성 또는 `brotli-asgi` 계열 참고
- ASP.NET Core → `Content-Encoding` 검사 후 `GZipStream`으로 `HttpContext.Request.Body` 교체하는 미들웨어
- Express → `body-parser`가 `Content-Encoding: gzip`을 기본 처리(`inflate: true`)

---

## 2. 응답 압축 (nginx 담당)

JSON 응답에 gzip을 켭니다. 클라이언트는 이미 `Accept-Encoding: gzip, deflate, br`을 보내도록 적용 완료(아래 "클라이언트 적용분" 참고)이므로, **nginx에서 켜기만 하면 즉시 적용**됩니다.

```nginx
gzip              on;
gzip_types        application/json;
gzip_min_length   1024;
gzip_comp_level   5;
gzip_proxied      any;
gzip_vary         on;
```

효과가 큰 응답: 세션 목록·세션 본문(`GET /api/v1/agent/sessions/{id}` — 대화 전문), 프로젝트 목록, 도구 정책.

## 3. SSE는 압축에서 제외 (nginx 담당) — 회귀 방지

**`text/event-stream`에는 gzip을 걸면 안 됩니다.** gzip은 압축 버퍼가 찰 때까지 출력을 모으므로, 이벤트 스트림에 걸면 토큰이 실시간으로 흐르지 않고 뭉텅이로 늦게 도착합니다. 사용자 체감이 눈에 띄게 나빠집니다.

`gzip_types`에 `text/event-stream`을 **넣지 마세요**(위 설정은 `application/json`만 지정하므로 안전). 추가로 SSE 위치에는 버퍼링을 끄는 것을 권장합니다:

```nginx
location /api/v1/agent/chat {
    proxy_buffering off;
    gzip            off;
    proxy_set_header Connection '';
    proxy_http_version 1.1;
    chunked_transfer_encoding off;
}
```

> 클라이언트는 SSE 요청에 대해 압축을 **광고하지 않도록** 이미 분리해 두었습니다(아래 참고). 다만 프록시 설정이 응답 Content-Type 기준으로 동작하는 경우가 있어, 서버 쪽에서도 제외해 두는 편이 안전합니다.

---

## 클라이언트 적용분 (구현 완료)

| 변경 | 내용 |
|------|------|
| 컨트롤플레인 전용 `HttpClient` 분리 | `AutomaticDecompression = DecompressionMethods.All` — 비스트리밍 REST 응답만 압축 수용 |
| 채팅 SSE 는 기존 클라이언트 유지 | `Accept-Encoding` 을 보내지 않음 → 서버가 SSE 를 압축할 여지를 주지 않음 |
| **요청 본문 gzip** | 구현 완료, **기본 꺼짐**. `AppSettings.CompressRequests = true` 로 활성화 |

**응답 클라이언트를 분리한 이유(실측)**: `AutomaticDecompression`은 **핸들러 단위** 설정이라 요청별로 끌 수 없습니다. 요청에 `Accept-Encoding: identity`를 직접 넣어도 핸들러가 뒤에 자기 값을 덧붙여 `identity, gzip, deflate, br`로 나갑니다. 그래서 인스턴스 자체를 나누는 것 외에 방법이 없습니다.

### 요청 압축 활성화 방법

서버에 위 **1번**이 반영된 것을 확인한 뒤, 설정 파일(`%APPDATA%\OhMyAgent\settings.json`)에서:

```json
{ "CompressRequests": true }
```

- 기본값 `false` — 서버가 미지원인 상태에서 켜면 400/415 로 요청이 실패하므로, **확인 전에는 켜지 마세요.**
- 본문 **32 KB 미만은 압축하지 않습니다**(임계값 `AgentApiClient.CompressRequestMinBytes`). 로그인·권한 승인 같은 작은 요청은 켜져 있어도 평문으로 나갑니다.
- 압축 레벨 `Optimal`. 308 KB 본문 기준 `Fastest` 대비 1.5 ms 더 쓰고 35 KB 더 줄어듭니다(실측). `SmallestSize` 는 4.4 ms 를 더 쓰고 1.6 KB 만 이득이라 채택하지 않았습니다.

**실제 와이어 검증** (로컬 리스너 + 실제 `AgentApiClient`, 200메시지 세션):

| 설정 | `Content-Encoding` | `Content-Type` | 와이어 크기 | 서버 복원 후 | 파싱 |
|---|---|---|---:|---:|:---:|
| `false` (기본) | (없음) | `application/json; charset=utf-8` | 234.7 KB | 234.7 KB | ✅ |
| `true` | `gzip` | `application/json; charset=utf-8` | **52.2 KB** | 234.7 KB | ✅ |

압축을 켜도 `Content-Type` 은 동일하며, 서버가 gzip 을 풀면 **바이트 단위로 같은 JSON** 이 나옵니다.

---

## 수용 기준 (서버 반영 후 확인 방법)

**1번 — 요청 압축**
```bash
# 평문 (기존 동작 유지 확인)
curl -X POST https://<host>/api/v1/agent/chat \
     -H 'Content-Type: application/json' -H "Authorization: Bearer $T" \
     -d '{"model":"...","stream":false,"messages":[...]}'

# gzip 본문 — 위와 동일한 결과가 나와야 함
printf '%s' "$BODY" | gzip > /tmp/body.gz
curl -X POST https://<host>/api/v1/agent/chat \
     -H 'Content-Type: application/json' -H 'Content-Encoding: gzip' \
     -H "Authorization: Bearer $T" --data-binary @/tmp/body.gz
```

**2번 — 응답 압축**
```bash
curl -s -D- -o /dev/null -H 'Accept-Encoding: gzip' \
     -H "Authorization: Bearer $T" https://<host>/api/v1/agent/sessions
# 기대: Content-Encoding: gzip
```

**3번 — SSE 미압축**
```bash
curl -s -D- -o /dev/null -H 'Accept-Encoding: gzip' -H 'Accept: text/event-stream' \
     -H "Authorization: Bearer $T" -X POST https://<host>/api/v1/agent/chat -d "$BODY"
# 기대: Content-Encoding 헤더 없음. 토큰이 즉시 흘러나옴
```

---

## 우선순위 제안

1. **1번(요청 압축)** — 효과가 가장 크고, 유일하게 API 앱 작업이 필요합니다. 여기부터 논의가 필요합니다.
2. **3번(SSE 제외 확인)** — 이미 그렇게 되어 있을 가능성이 높지만, 2번을 켜기 전에 확인해야 합니다.
3. **2번(응답 압축)** — 클라이언트는 준비됐으므로 nginx 한 줄로 끝납니다.
