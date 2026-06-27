# 도구(Tool) 시스템 설계

클라이언트가 "실제로 할 수 있는 동작"(파일·셸·시스템 조작)을 어떻게 구현·정리했는지 설명합니다.

## 한 줄 요약

**도구 1개 = `ITool` 인터페이스를 구현한 클래스 1개.** 전부 `Services/Tools/`에 모여 있고,
앱 시작 시 배열로 등록되어 → 모델에게 "할 수 있는 일" 목록으로 노출되고 → 모델이 호출하면
**3중 게이트(서버 정책 · 권한 · 샌드박스)**를 거쳐 클라이언트에서 실제 실행됩니다.

> 핵심: **"무엇을 할지"는 모델이 판단**하고, **"어떻게 실행할지"(OS 조작)는 클라이언트의 도구**가 담당합니다.

---

## 1. 디렉토리 구조

```
Services/
├── ITool.cs                 ← 도구 "계약"(인터페이스) — 모든 도구가 구현
├── IToolRegistry.cs         ← 레지스트리 계약
├── ToolRegistry.cs          ← 도구 모음 관리(이름→도구 조회, 서버용 스키마 생성)
├── ToolContext.cs           ← 실행 시 주변 환경(워크스페이스·권한모드) 전달
├── IToolPolicyService.cs    ┐  서버 도구 정책 게이트
├── ToolPolicyService.cs     ┘  (cached/realtime 허용 통제)
├── ScriptExecutor.cs        ← run_command 의 PowerShell/CMD 실행 엔진
├── SecurityValidator.cs     ← 위험 명령 차단
├── ToolCallJsonConverter.cs ← 도구 호출 인자(arguments) JSON 직렬화 변환
│
└── Tools/                   ← ★ 도구 구현체 20개
    ├── ReadFileTool.cs  WriteFileTool.cs  EditFileTool.cs
    ├── ListDirectoryTool.cs  GlobTool.cs  GrepTool.cs
    ├── CreateDirectoryTool.cs  MoveTool.cs  CopyTool.cs  DeleteTool.cs
    ├── RunCommandTool.cs
    ├── GetEnvironmentTool.cs  ClipboardReadTool.cs  ClipboardWriteTool.cs
    ├── ListProcessesTool.cs  ListProcessesMemoryKbTool.cs
    ├── StartProcessTool.cs  KillProcessTool.cs
    ├── HttpFetchTool.cs  ScreenshotTool.cs
    ├── ToolSchemas.cs       ← 공통 JSON 스키마 파싱·인자 추출 헬퍼
    └── GlobMatcher.cs       ← glob 보조 유틸

Models/Agent/
├── ToolCall.cs              ← 모델이 보낸 "이 도구 불러라" 요청
├── ToolResult.cs           ← 도구 실행 결과(성공/실패 + 내용)
├── ToolSchema.cs           ← 서버로 보낼 도구 선언(이름/설명/파라미터)
├── ToolPolicy.cs           ← 서버 정책 모델
└── AgentEnums.cs           ← ToolRisk(위험도) enum 등
```

> 핵심 3개만 알면 됩니다: **계약(`Services/ITool.cs`) + 구현체 모음(`Services/Tools/`) + 등록(`App.xaml.cs`)**.

---

## 2. 핵심 계약 — `ITool` (도구 1개 = 이 5개만 채움)

```csharp
public interface ITool
{
    string Name { get; }                  // 모델에 노출되는 도구명 (예: "write_file")
    string Description { get; }            // 모델이 보고 "언제 쓸지" 판단
    JsonElement ParametersSchema { get; }  // 파라미터 JSON Schema
    ToolRisk Risk { get; }                 // 위험도 → 권한 게이트 기준
    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default);
}
```

- 모든 도구는 **stateless 싱글톤**: 상태를 들지 않고, 인자를 받아 실행만.
- 위험도 `ToolRisk`는 4단계: `ReadOnly` · `Write` · `Execute` · `Destructive`.

---

## 3. 도구가 모델에게 노출되는 흐름

```
App.xaml.cs 에서 도구 배열 생성
   var tools = new ITool[] { new ReadFileTool(), new WriteFileTool(), ... };  // 20개
        ↓
   ToolRegistry(tools)            ← 이름으로 조회(TryGet) + 스키마 목록 생성(ToSchemas)
        ↓
   AgentOrchestrator 가 요청 만들 때 tools.ToSchemas() 생성
        ↓
   ★ 서버 정책 노출 필터   .Where(t => ToolPolicyService.IsExposed(t.Name))
   └ 비활성 도구는 모델이 아예 못 봄(스키마에서 제외). 미로드/realtime → 전체 노출(현행과 동일)
        ↓
   POST /api/v1/agent/chat 의 "tools" 필드로 서버→모델 전달
   → 모델이 "이런 도구들을 쓸 수 있구나" 인지
```

즉 클라가 **"나 이런 거 할 수 있어"를 스키마(데이터)로** 모델에 알려주고, **실행 코드는 클라에** 둡니다.

> **노출·실행 일관성**: 노출 필터(`IsExposed`)는 실행 게이트(`EvaluateAsync`)와 **동일 규칙**(cached 모드: disabled 우선 → enabled 화이트리스트)을 씁니다.
> 비활성 도구는 모델이 **보지도 못하고**(노출 차단) 설령 호출해도 **실행도 차단**됩니다. 자세한 설계는 [`server-controlled-security-and-tools.md`](server-controlled-security-and-tools.md) 참조.

---

## 4. 도구 실행 흐름 — 3중 게이트

모델이 `tool_call`(예: write_file)을 보내면 `AgentOrchestrator`가 이렇게 처리합니다:

```
모델: "write_file 불러라"
   ↓
① 서버 도구 정책 게이트   ToolPolicyService.EvaluateAsync()
   └ 차단되면 → 실행 안 함, 사유를 모델에 피드백
   ↓
② 로컬 권한 게이트       PermissionService — 위험도 × 권한모드(수동/안전자동/전체자동)
   └ 수동 승인 모드면 → 화면에 승인 카드, 사용자 "허용" 필요
   ↓
③ 샌드박스 검증          ToolContext.Workspace.ResolvePath()
   └ 작업 디렉토리 밖 경로면 → 거부(throw)
   ↓
   WriteFileTool.ExecuteAsync()  ← 여기서 실제 파일 생성 (OS 조작)
   ↓
   ToolResult → tool 메시지로 서버에 회신 → 루프 반복
```

> **중앙 집중 오류 처리**: 도구가 던진 예외는 `AgentOrchestrator.ExecuteCallAsync` 한 곳에서
> 전부 `ToolResult.Fail`로 변환됩니다 → 앱이 죽지 않고 모델에 에러를 피드백. 개별 도구는 정상 경로만 구현.

---

## 5. 내장 도구 20개

| 그룹 | 도구 | 위험도 |
|------|------|--------|
| 파일 | `read_file` · `write_file` · `edit_file` | ReadOnly / Write |
| 탐색 | `list_directory` · `glob` · `grep` | ReadOnly |
| 파일조작 | `create_directory` · `move` · `copy` · `delete` | Write / Destructive |
| 셸 | `run_command` | Execute |
| 시스템 | `get_environment` · `clipboard_read` · `clipboard_write` | ReadOnly / Write |
| 프로세스 | `list_processes` · `list_processes_memory_kb` · `start_process` · `kill_process` | ReadOnly / Execute / Destructive |
| 네트워크·비전 | `http_fetch` · `screenshot` | Execute / ReadOnly |

> 전부 BCL/WPF/WinForms 내장 기능만 사용 — 추가 NuGet 의존성 0.

---

## 6. 지원 인프라

| 파일 | 역할 |
|------|------|
| `ToolContext` | 실행 시 워크스페이스(샌드박스)·권한모드를 도구에 전달 (도구 내부에 DI 없음) |
| `ToolSchemas` | 도구 공통 JSON 스키마 파싱·인자 추출 헬퍼 |
| `ScriptExecutor` | `run_command`의 PowerShell/CMD 실제 실행 엔진 |
| `SecurityValidator` | 위험 명령 패턴 차단 |
| `ToolCallJsonConverter` | `tool_call.arguments`를 서버 계약대로 JSON 문자열↔객체 변환 |
| `IWorkspaceContext` | 멀티루트 샌드박스 — `ResolvePath`/`IsInsideWorkspace`로 경로 탈출 차단 |

---

## 7. 새 도구 추가하는 법 (2단계)

```
1) Services/Tools/MyNewTool.cs 생성 → ITool 구현 (Name/Description/ParametersSchema/Risk/ExecuteAsync)
2) App.xaml.cs 의 tools 배열에 new MyNewTool() 한 줄 추가
   → 끝. 자동으로 모델에 노출 + 권한/샌드박스 게이트 적용
```

### 예: `ListProcessesMemoryKbTool` (실제 추가 사례)

```csharp
public sealed class ListProcessesMemoryKbTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """{"type":"object","properties":{"name_filter":{"type":"string"}}}""");

    public string Name => "list_processes_memory_kb";
    public string Description => "List running processes with name, PID, and working-set memory (KB).";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var filter = ToolSchemas.GetString(args, "name_filter");
        // Process.GetProcesses() … WorkingSet64 / 1024 …
        return Task.FromResult(ToolResult.Json(new { count, processes }));
    }
}
```

App.xaml.cs:
```csharp
var tools = new ITool[]
{
    ...
    new ListProcessesTool(),
    new ListProcessesMemoryKbTool(),   // ← 한 줄 추가
    ...
};
```

---

## 8. 설계 원칙 요약

- **단일 계약**: 도구 = `ITool` 구현 1개 → 일관성·확장성.
- **stateless 싱글톤**: 도구는 상태 없이 인자만 받아 실행.
- **관심사 분리**: "무엇을 할지"=모델 / "어떻게 실행"=클라 도구 / "해도 되나"=3중 게이트.
- **중앙 집중 오류 처리**: 도구는 정상 경로만, 예외는 오케스트레이터가 `ToolResult.Fail`로 변환.
- **데이터 vs 코드 분리**: 도구 *선언(스키마)*은 서버로, *실행 코드*는 클라에(OS 접근 때문).
- **최소 의존성**: BCL/WPF/WinForms 내장 기능만, 추가 NuGet 0.

> 관련 문서: 서버 도구 정책은 [`server-tool-policy-api.md`](server-tool-policy-api.md),
> 전체 아키텍처는 [`../README.md`](../README.md) 참조.
