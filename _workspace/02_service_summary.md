# 02 Service Summary — 의존성 정리 + STJ 통일 + 경고 제거

작업: 기능 변경 없음. 순수 의존성 정리 / Newtonsoft → System.Text.Json 이관 / 빌드 경고 제거.

## 변경 파일

| 파일 | 변경 내용 |
|------|----------|
| `OhMyAgent.AiAgent.Client/OhMyAgent.AiAgent.Client.csproj` | Newtonsoft.Json / System.Drawing.Common 제거, CommunityToolkit.Mvvm 8.3.2 → 8.4.0 |
| `Services/SettingsService.cs` | JsonConvert → System.Text.Json, 영속 전용 옵션 `PersistenceOptions` 신설 |
| `Models/WorkspaceHistoryEntry.cs` | STJ `[JsonPropertyName]` 을 snake_case → PascalCase 로 교정(디스크 호환) |
| `Services/IToolRegistry.cs` | `TryGet` 시그니처에 `[MaybeNullWhen(false)]` 추가 (CS8767 해소, 한 줄) |
| `Services/ToolRegistry.cs` | 변경 없음 — 이미 `[MaybeNullWhen(false)]` 보유, 인터페이스 일치로 경고 해소 |
| `Services/BinaryIntegrityService.cs` | `Valid` 케이스 `manifest = loaded ?? throw ...` 로 null 가드 (CS8602 해소) |

## 패키지 변경

- **제거**: `Newtonsoft.Json` 13.0.3 (코드 마이그레이션 완료 후 제거)
- **제거**: `System.Drawing.Common` 8.0.0 — `UseWindowsForms=true` 가 System.Drawing 을 이미 제공하여 NU1510 발생. 패키지 제거 후에도 App.xaml.cs 트레이 아이콘 코드는 WinForms 가 제공하는 System.Drawing 으로 그대로 컴파일됨.
- **업그레이드**: `CommunityToolkit.Mvvm` 8.3.2 → **8.4.0** (NuGet 최신 stable, 소스 생성기 호환).

## STJ 마이그레이션 — 디스크 호환 보장 방법 (핵심)

기존 `%APPDATA%/OhMyAgent/settings.json` 은 Newtonsoft 가 작성한 **PascalCase + 정수 enum** 포맷이다.
실제 디스크 샘플:
```json
{
  "Hotkey": { "Modifiers": 2, "KeyCode": 18 },
  "Opacity": 1.0, "SchemaVersion": 4, "WorkspaceRoot": "",
  "PermissionMode": 0, "MaxIterations": 25,
  "ServerBaseUrl": "http://localhost:8080", "AuthScheme": "Bearer",
  "AuthToken": "", "ModelId": "corp-llm-32b", "MaxTokens": 4096,
  "UserDisplayName": "asd", "RecentWorkspaces": []
}
```

STJ 기본값(Web preset)은 **camelCase** 라 그대로 쓰면 모든 키가 깨져 사용자 설정이 날아간다. 따라서 영속 전용 옵션을 별도로 두고 Newtonsoft 와 동일 포맷을 강제:

`SettingsService.PersistenceOptions` (에이전트 와이어용 `AgentJson.Options` 와 의도적으로 분리):
- `PropertyNamingPolicy = null` → **PascalCase 유지** (Newtonsoft 기본과 동일, 가장 중요)
- `WriteIndented = true` → Newtonsoft `Formatting.Indented` (둘 다 2-space) 대응
- enum 변환기 미등록 → STJ/Newtonsoft 공통 기본인 **정수 직렬화** 유지 (`Modifiers`, `PermissionMode`, `KeyCode`)
- `PropertyNameCaseInsensitive = true` → 구파일/대소문자 변형 로드 견고성
- `ReadCommentHandling = Skip`, `AllowTrailingCommas = true` → 읽기 손상 내성
- `DateTimeOffset` 은 STJ/Newtonsoft 모두 ISO 8601 round-trip 이므로 호환 (RecentWorkspaces 엔트리용)

`WorkspaceHistoryEntry` 보정: 기존 코드의 `[JsonPropertyName("path"/"display_name"/"last_used_utc")]` 는 **Newtonsoft 가 무시**했으므로 디스크에는 PascalCase(`Path`/`DisplayName`/`LastUsedUtc`)로 기록되어 있었다. STJ 로 전환하면 이 어트리뷰트가 발효되어 snake_case 로 바뀌어 호환이 깨진다. → 어트리뷰트를 **PascalCase 명시**로 교정하여 직렬화기 정책과 무관하게 디스크 포맷 고정.

비동기 파일 IO 패턴(`Task.Run` + `_ioLock` + `ConfigureAwait(false)`)은 그대로 유지.

## 빌드 경고 제거

- **CS8767** (`ToolRegistry.TryGet`): 인터페이스 `IToolRegistry.TryGet` 에 `[MaybeNullWhen(false)]` 가 없어 구현부와 nullable 어노테이션이 불일치. 인터페이스 시그니처 한 줄에 어트리뷰트 추가하여 일치. 구현부는 이미 보유 → 무변경.
- **CS8602** (`BinaryIntegrityService.cs` ~346): `Valid` 케이스에서 `manifest = loaded` (loaded 가 `IntegrityManifest?`) 이후 `manifest.Entries` 역참조 시 null 경고. `loaded ?? throw new AgentException(...)` 로 가드하여 non-null 보장.

## 빌드 확인

`dotnet build` 결과 **내 담당 파일 5종 + IToolRegistry.cs 에 error/warning 0**, NU1510/CS8767/CS8602 전부 사라짐.

남은 빌드 error 는 `Services/Tools/ClipboardReadTool.cs`, `ClipboardWriteTool.cs` 의 `Application` 모호 참조(CS0104, `System.Windows.Forms.Application` vs `System.Windows.Application`). 이는 **내 스코프 밖(Services/Tools)** 이며 기존부터 존재하던 `UseWindowsForms=true`(이번에 미변경) 로 인한 것 — 다른 에이전트 담당. 내 변경(JSON/Drawing/Mvvm)과 무관함.
