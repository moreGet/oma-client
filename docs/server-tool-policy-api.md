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

## 도구 인벤토리 — 서버가 제어할 수 있는 도구 전체 (37개)

`enabled`/`disabled` 에 넣을 **정확한 도구명**입니다. 이름은 클라이언트 코드의 `ITool.Name` 이 정본이며, 오타는 조용히 무시됩니다(존재하지 않는 이름은 아무 도구도 매치하지 않음).

**위험도**는 클라이언트의 로컬 승인 게이트 기준입니다 — `ReadOnly` 는 승인 없이 실행, `Write`/`Destructive`/`Execute` 는 권한 모드에 따라 실행 전 승인 카드가 뜹니다. 서버 정책은 이와 **별개의 상위 게이트**입니다(정책이 차단하면 모델에 도구 자체가 노출되지 않습니다).

**노출** 열: `D`=데스크톱 클라이언트, `H`=헤드리스 호스트.

| 도구명 | 위험도 | 노출 | 분류 | 비고 |
|--------|--------|:---:|------|------|
| `read_file` | ReadOnly | D·H | 파일 | |
| `write_file` | Write | D·H | 파일 | |
| `edit_file` | Write | D·H | 파일 | |
| `list_directory` | ReadOnly | D·H | 파일 | |
| `glob` | ReadOnly | D·H | 파일 | |
| `grep` | ReadOnly | D·H | 파일 | |
| `create_directory` | Write | D·H | 파일 | |
| `move` | Destructive | D·H | 파일 | |
| `copy` | Destructive | D·H | 파일 | |
| `delete` | Destructive | D·H | 파일 | |
| `run_command` | Execute | D·H | 셸·프로세스 | 위험 명령 차단 목록이 별도 적용(`/api/v1/security/command-policy`) |
| `start_process` | Execute | D·H | 셸·프로세스 | |
| `kill_process` | Destructive | D·H | 셸·프로세스 | |
| `list_processes` | ReadOnly | D·H | 셸·프로세스 | |
| `list_processes_memory_kb` | ReadOnly | D·H | 셸·프로세스 | |
| `get_environment` | ReadOnly | D·H | 셸·프로세스 | 정보 유출 경로라 모드와 무관하게 **항상 승인**(`clipboard_read`·`screenshot` 과 같은 취급) |
| `http_fetch` | Execute | D·H | 네트워크 | 사내 HTTP 호출 |
| `read_csv` | ReadOnly | D·H | 문서·데이터 | |
| `write_csv` | Write | D·H | 문서·데이터 | |
| `read_excel` | ReadOnly | D·H | 문서·데이터 | |
| `write_excel` | Write | D·H | 문서·데이터 | |
| `read_pdf` | ReadOnly | D·H | 문서·데이터 | |
| `read_document` | ReadOnly | D·H | 문서·데이터 | Word |
| `read_pptx` | ReadOnly | D·H | 문서·데이터 | |
| `write_pptx` | Write | D·H | 문서·데이터 | |
| `read_hwpx` | ReadOnly | D·H | 문서·데이터 | 한글 HWPX |
| `compress_files` | Write | D·H | 압축 | |
| `extract_archive` | Write | D·H | 압축 | zip-slip 차단 |
| `clipboard_read` | ReadOnly | **D** | 시스템·UI | 데스크톱 전용. 모드와 무관하게 **항상 승인** |
| `clipboard_write` | Write | **D** | 시스템·UI | 데스크톱 전용 |
| `screenshot` | ReadOnly | **D** | 시스템·UI | 데스크톱 전용. 모드와 무관하게 **항상 승인** |
| `manage_todos` | ReadOnly | D·H | 에이전트 메타 | 작업 계획 추적. 서브에이전트에는 비노출 |
| `schedule_wakeup` | ReadOnly | D·H | 에이전트 메타 | 자율 페이싱 `/loop` 의 다음 실행 예약. 서브에이전트에는 비노출 |
| `task` | ReadOnly | D·H | 에이전트 메타 | 서브에이전트 위임. 서브에이전트에는 비노출(무한 중첩 방지) |
| `discover_agents` | ReadOnly | **H** | A2A | 헤드리스 전용 — 에이전트 레지스트리 조회 |
| `ask_agent` | Execute | **H** | A2A | 헤드리스 전용 — 다른 에이전트에 작업 위임 |
| `generate_image` | Write | D·H | 이미지 | **서버 엔드포인트 대기** — `docs/server-image-api.md` 참고 |

**합계**: 데스크톱 **35개** · 헤드리스 **34개** · 고유 **37개**.

> `task` 로 위임된 **서브에이전트**는 이 목록의 부분집합만 받습니다(`TaskTool.AllowedToolNames`) — `ReadOnly` 조사 도구만이며 `task`·`manage_todos`·`schedule_wakeup`·`generate_image` 는 제외됩니다. **서버 정책은 서브에이전트에도 그대로 적용됩니다**(같은 게이트를 지납니다).

### 서버 정책을 짤 때 참고

- **분류 단위로 끄는 것이 실용적입니다** — 예: 셸 실행을 막고 싶으면 `run_command`·`start_process`·`kill_process` 를 함께 넣어야 합니다. 하나만 막으면 다른 경로로 우회됩니다.
- `read_*` 계열만 허용하는 **조사 전용 프로필**을 만들 수 있습니다(`enabled` 화이트리스트에 `read_file`·`glob`·`grep`·`list_directory` + 문서 읽기 계열).
- `disabled` 가 `enabled` 보다 **우선**하므로, 넓게 허용하고 위험한 것만 빼는 운영이 가장 관리하기 쉽습니다.
- 도구가 추가되면 이 표도 갱신됩니다. **서버가 `enabled` 화이트리스트를 쓰는 경우, 새 도구는 자동으로 차단됩니다** — 신규 도구 배포 시 화이트리스트 갱신을 잊지 마세요.

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
- 도구명은 클라 내장 도구 식별자와 정확히 일치해야 함. **현재 클라 내장 도구 = 32개**(정책 `enabled`/`disabled`는 이 이름들을 참조):
  - **파일(10)**: `read_file`, `write_file`, `edit_file`, `list_directory`, `glob`, `grep`, `create_directory`, `move`, `copy`, `delete`
  - **셸·시스템(10)**: `run_command`, `get_environment`, `clipboard_read`, `clipboard_write`, `list_processes`, `list_processes_memory_kb`, `start_process`, `kill_process`, `http_fetch`, `screenshot`
  - **문서·데이터(9)**: `read_csv`, `write_csv`, `read_excel`, `write_excel`, `read_pdf`, `read_document`, `read_pptx`, `write_pptx`, `read_hwpx`
  - **압축(2)**: `compress_files`(zip 생성), `extract_archive`(zip 해제)
  - **에이전트 메타(1)**: `manage_todos` (다단계 작업 계획 추적)
  > 이전 문서판은 앞 20개만 나열했음 — 문서·데이터 6개와 `manage_todos` 1개(총 7개)가 누락돼 있었다. 정책으로 이들까지 통제하려면 서버 도구 목록에 반드시 포함할 것.

---

## 향후 추가 예정 도구 (로드맵 · 아직 미구현)

> 아래는 **아직 클라에 구현되지 않은** 계획된 도구다. 구현되면 클라가 스키마를 서버로 보내기 시작하므로,
> 서버 정책 카탈로그(enabled/disabled 참조용)에 **이름을 미리 등록**해 두면 출시 즉시 통제할 수 있다.
> 이름은 확정 전 잠정값이며, 실제 구현 시 이 문서와 위 "현재 도구" 목록을 함께 갱신한다.
> 전부 순수 관리코드/OS 내장 기능만 사용해 **폐쇄망 적합**을 원칙으로 한다.

> ✅ **구현 완료**: `compress_files`·`extract_archive`(zip), `read_pptx`·`write_pptx`(PowerPoint), `read_hwpx`(한글) — 2026-07-14 반영, 위 "현재 도구" 참조.

### 1순위 — 명백한 공백 보완
| 도구명(잠정) | 위험도 | 용도 | 구현 방식 |
|------|:---:|------|------|
| `write_document`  | Write | Word `.docx` 생성/수정 | OpenXML SDK(관리) — 현재 read_document(읽기)만 존재 |
| `write_hwpx`      | Write | 한글 `.hwpx` 생성 | OWPML zip+XML 직접 구성(고난도) — 구형 바이너리 `.hwp`는 별도 |

### 2순위 — 유용
| 도구명(잠정) | 위험도 | 용도 | 구현 방식 |
|------|:---:|------|------|
| `read_json` / `write_json` | ReadOnly / Write | JSON 읽기·쓰기 | `System.Text.Json`(BCL) |
| `read_xml`        | ReadOnly | XML 읽기·질의 | BCL |
| `merge_pdf` / `split_pdf` | Write | PDF 병합·분할 | PdfPig/PdfSharp(관리) — read_pdf 보완 |
| `read_image_text` | ReadOnly | 이미지/스크린샷 → 텍스트(OCR) | `Windows.Media.Ocr`(OS 내장, 오프라인·한국어팩) |
| `send_email`      | Execute | 사내 SMTP 메일 발송 | `System.Net.Mail`(BCL) |
| `replace_in_files`| Write | 여러 파일 일괄 문자열 치환 | BCL(grep 보완) |

### 3순위 — 강력하나 신중(고위험)
| 도구명(잠정) | 위험도 | 용도 | 구현 방식 |
|------|:---:|------|------|
| `create_scheduled_task` | Execute | Windows 작업 스케줄러 등록 | schtasks/TaskScheduler |
| `ui_automate`     | Execute | 레거시 앱 자동화(키 입력·클릭, RPA) | UIAutomation — 불안정성 주의 |
| `get_system_info` | ReadOnly | 디스크·메모리·OS 진단 정보 | BCL/WMI |
| `registry_read`   | ReadOnly | 레지스트리 설정 조회(쓰기는 제외) | `Microsoft.Win32.Registry` |
| `clipboard_read_image` | ReadOnly | 클립보드 이미지 읽기(현재 텍스트만) | WPF Clipboard(STA) |

> 위험도(ToolRisk) 매핑은 로컬 권한 게이트(②)에도 그대로 적용된다: Write/Execute/Destructive는 권한 모드에 따라 실행 전 승인 카드를 띄운다.
> 서버 정책(①)에서 이름 기반 enabled/disabled로 조직별 노출/실행을 통제할 수 있다.

## 우선순위
- 선택 기능. 미구현이어도 클라 정상 동작(전체 허용). 통제 강화가 필요해질 때 도입.
