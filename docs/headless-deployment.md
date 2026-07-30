# 헤드리스 에이전트 배포 · 운영

대상: `OhMyAgent.AiAgent.Host` 를 리눅스/Windows 서버에 상주시키는 운영자.

> 관련: A2A·레지스트리 계약 `a2a-registry.md` · 도구 정책 `server-tool-policy-api.md` ·
> 서비스 계정 요구 `server-service-account-api.md`

---

## 1. 인증 모델 — 왜 env 주입인가

헤드리스는 토큰을 `OHMYAGENT_AUTH_TOKEN` 환경 변수로 받습니다. **대화형 로그인 명령은 없습니다.**

의도된 설계입니다. 데몬에 로그인 프롬프트를 붙이면 만료될 때마다 사람이 붙어야 하고, 무인 재시작이
불가능해집니다. 토큰 주입은 systemd·컨테이너 시크릿과 맞물리는 표준 방식입니다.

리눅스에서는 토큰이 **디스크에 저장되지 않습니다**. 토큰 암호화가 DPAPI(Windows 전용) 기반이고,
평문 폴백은 금지돼 있기 때문입니다(`TokenProtector`). 즉 리눅스는 **매 기동 시 주입**이 유일한 경로입니다.

> Windows 서버에 배포하면 DPAPI가 동작해 `%APPDATA%/OhMyAgent/settings.json`에 암호화 저장되고,
> env가 없으면 저장된 값을 씁니다. 다만 무인 운영에서는 리눅스와 동일하게 env 주입을 권합니다 —
> 재시작만으로 토큰을 바꿀 수 있어야 회전이 단순해집니다.

### 어떤 계정의 토큰을 넣을 것인가

서버 도구 정책은 **계정 단위**로 적용됩니다(JWT의 member로 결정). 따라서 주입하는 토큰의 계정이
곧 그 에이전트의 권한 경계입니다.

- **사람 계정 토큰을 넣지 마세요** — 그 사람의 권한을 그대로 상속합니다
- 에이전트(또는 등급)마다 **전용 계정**을 만들고 그 계정에 도구 정책을 겁니다

```
svc-agent-review   → 읽기 도구만 허용
svc-agent-build    → run_command 포함 허용
```

장수 자격증명(API 키)은 아직 서버에 없습니다 — `server-service-account-api.md` 참조.

---

## 2. 종료 코드

헤드리스는 사람이 화면을 보고 있지 않으므로 실패를 **종료 코드로** 드러냅니다.

| 코드 | 의미 | 조치 |
|---|---|---|
| `0` | 정상 종료 | — |
| `69` | 서버 미도달 | 재시작으로 회복 가능 — 서버·네트워크 확인 |
| `77` | **인증 실패**(토큰 만료·무효·폐기) | **새 토큰 주입 후 재시작** |
| `78` | 설정 오류(필수 env 누락 등) | 유닛 파일 수정 |

인증 실패는 두 지점에서 검출됩니다:

- **기동 시** — 토큰 유효성을 선검사하고, 죽었으면 즉시 77로 종료합니다
- **런타임** — 401/403이 **연속 3회** 누적되면 종료합니다. 성공이 한 번이라도 끼면 카운터가
  초기화되므로, 서버 재기동·키 회전 중의 일시적 401은 흡수됩니다

> 이 처리가 없던 시절에는 토큰이 만료돼도 프로세스가 계속 떠 있으면서 모든 요청만 실패하는
> "좀비" 상태가 됐고, 종료 코드가 0이라 systemd가 개입할 수 없었습니다.

---

## 3. systemd 유닛

`/etc/systemd/system/ohmyagent-agent@.service` — 템플릿 유닛(인스턴스별 `%i`).

```ini
[Unit]
Description=OhMyAgent 헤드리스 에이전트 (%i)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=ohmyagent
Group=ohmyagent

# 토큰은 유닛 파일에 직접 쓰지 않는다 — 파일 권한 0600 으로 분리하고 EnvironmentFile 로 읽는다.
# (유닛 파일은 통상 world-readable 이라 토큰을 넣으면 전체 사용자에게 노출된다.)
EnvironmentFile=/etc/ohmyagent/%i.env

ExecStart=/opt/ohmyagent/OhMyAgent.AiAgent.Host

# 69(서버 미도달)는 재시작으로 회복 가능 → on-failure 로 흡수.
# 77(인증)·78(설정)은 사람 개입 전까지 재시작해도 같은 실패를 반복하므로 제외한다.
Restart=on-failure
RestartSec=10s
RestartPreventExitStatus=77 78

# 재시작 폭풍 방지 — 5분 내 5회 실패하면 멈추고 사람을 부른다.
StartLimitIntervalSec=300
StartLimitBurst=5

# 샌드박스 — 에이전트가 워크스페이스 밖을 건드리지 못하게 OS 수준에서도 조인다.
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
NoNewPrivileges=yes
ReadWritePaths=/srv/agent-ws/%i

[Install]
WantedBy=multi-user.target
```

`RestartPreventExitStatus=77 78` 이 핵심입니다. 인증·설정 실패는 재시작해도 낫지 않으므로
**멈춰서 알람이 울리게** 두고, 회복 가능한 실패만 자동 재시작합니다.

### 환경 파일

`/etc/ohmyagent/review.env` — 권한 `0600`, 소유자 `ohmyagent`.

```bash
OHMYAGENT_SERVER_URL=http://ai-server.corp:8080
OHMYAGENT_AUTH_TOKEN=<서비스 계정 토큰>
OHMYAGENT_WORKSPACE=/srv/agent-ws/review
OHMYAGENT_HEADLESS_APPROVAL=deny

# A2A 리스너 모드로 띄울 때만
OHMYAGENT_LISTEN=http://0.0.0.0:8080/
OHMYAGENT_ADVERTISE_URL=http://10.0.0.5:8080
OHMYAGENT_AGENT_NAME=review-agent
OHMYAGENT_CAPABILITIES=code-review,korean-nlp
OHMYAGENT_A2A_MODE=broker
```

```bash
install -d -m 0700 -o ohmyagent -g ohmyagent /etc/ohmyagent
install -m 0600 -o ohmyagent -g ohmyagent review.env /etc/ohmyagent/review.env

systemctl daemon-reload
systemctl enable --now ohmyagent-agent@review
```

> `OHMYAGENT_ADVERTISE_URL`은 `0.0.0.0`이면 기동이 거부됩니다 — 다른 에이전트가 접속할 수 있는
> 실제 주소여야 합니다.

### systemd 크리덴셜을 쓰는 경우

`EnvironmentFile` 대신 `LoadCredential`을 쓰면 토큰이 프로세스 환경에 남지 않아 `/proc/<pid>/environ`
노출을 막을 수 있습니다. 다만 Host는 **env만 읽으므로** 래퍼 스크립트가 필요합니다:

```ini
LoadCredential=token:/etc/ohmyagent/review.token
ExecStart=/bin/sh -c 'OHMYAGENT_AUTH_TOKEN=$(cat "$CREDENTIALS_DIRECTORY/token") exec /opt/ohmyagent/OhMyAgent.AiAgent.Host'
```

---

## 4. 토큰 회전

무중단 회전은 **서버가 키 2개 동시 유효를 지원해야** 성립합니다(현재 미지원 — `server-service-account-api.md`).
지원 전까지는 짧은 다운타임을 감수합니다.

```bash
# 1) 새 토큰 발급 후 환경 파일 갱신
install -m 0600 -o ohmyagent -g ohmyagent new.env /etc/ohmyagent/review.env

# 2) 재시작 — 기동 선검사가 새 토큰을 검증한다
systemctl restart ohmyagent-agent@review

# 3) 정상 기동 확인 후 구 토큰 폐기
systemctl status ohmyagent-agent@review
```

리스너 모드는 종료 시 레지스트리에서 best-effort로 자기를 해제하므로, 재시작 중에는 다른 에이전트의
발견 목록에서 빠집니다. 재등록 시 `(owner, name)` upsert라 `agent_id`는 유지됩니다.

---

## 5. 운영 점검

```bash
# 인증 실패로 멈춘 인스턴스 찾기 (77 = 새 토큰 필요)
systemctl show ohmyagent-agent@review -p ExecMainStatus

# 로그
journalctl -u ohmyagent-agent@review -f
```

애플리케이션 로그는 `~/.config/OhMyAgent/logs/app-yyyyMMdd.log`(서비스 계정 홈) 에도 남습니다.
`ProtectHome=yes` 를 쓰면 홈이 가려지므로, 로그를 남기려면 `ReadWritePaths` 에 로그 경로를 추가하거나
journald 만 사용하세요.

| 증상 | 원인 | 조치 |
|---|---|---|
| 기동 즉시 종료 코드 77 | 토큰 만료·무효 | 새 토큰 주입 후 재시작 |
| 기동 즉시 종료 코드 69 | 서버 미도달 | `OHMYAGENT_SERVER_URL`·네트워크 확인 |
| 모든 도구가 차단됨 | 도구 정책 조회 실패 → fail-closed | 서버 `/tools/policy` 상태 확인. 404(미구현)면 전체 허용이 정상 |
| A2A 수신이 전부 401 | broker 모드인데 등록 실패 | 레지스트리 등록 로그 확인 — broker 모드는 등록 성공이 전제 |
| 재시작 반복 후 멈춤 | `StartLimitBurst` 도달 | 원인 해결 후 `systemctl reset-failed` |
