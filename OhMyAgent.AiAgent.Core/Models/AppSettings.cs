using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

public class AppSettings
{
    // 기존 — 유지
    public HotkeySettings Hotkey { get; set; } = HotkeySettings.Default;
    public double Opacity { get; set; } = 1.0;
    public int SchemaVersion { get; set; } = 6;   // bump 5 -> 6 (AuthToken DPAPI 암호화)

    // MCP 서버 은퇴로 McpPort/McpEnabled 제거됨 (v3 마이그레이션에서 drop).

    // 신규 (Phase 1)
    public string WorkspaceRoot { get; set; } = "";            // 주 루트(primary). 첫 활성 폴더와 동기화. empty => Desktop fallback
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Manual;
    public int MaxIterations { get; set; } = 25;
    public string ServerBaseUrl { get; set; } = "http://localhost:8080";
    public string AuthScheme { get; set; } = "Bearer";          // "Bearer" | "ApiKey"
    public string ModelId { get; set; } = "";

    // ── 인증 토큰: 메모리엔 평문, 디스크엔 DPAPI 암호문 ──
    //
    // v6 이전에는 이 값이 settings.json 에 평문으로 저장돼, 사용자 권한의 아무 프로세스나 읽어
    // 토큰 수명 내내 사용자를 사칭할 수 있었다. 앱 코드 전체가 평문 AuthToken 을 쓰므로
    // 런타임 표면은 그대로 두고 직렬화 경계에서만 암복호화한다.

    /// <summary>런타임 평문 토큰. 디스크에 나가지 않는다(<see cref="AuthTokenProtected"/> 가 대신 저장됨).</summary>
    [JsonIgnore]
    public string AuthToken { get; set; } = "";

    /// <summary>디스크 저장용 DPAPI 암호문(base64). <see cref="SettingsService"/> 가 저장 직전에 채운다.</summary>
    [JsonPropertyName("AuthTokenProtected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthTokenProtected { get; set; }

    /// <summary>
    /// v5 이하 파일의 평문 "AuthToken" 을 읽기 위한 마이그레이션 전용 필드.
    /// 마이그레이션 후 null 로 비워 다시 기록되지 않게 한다(= 파일에서 평문 키가 사라진다).
    /// </summary>
    [JsonPropertyName("AuthToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAuthToken { get; set; }

    // 신규 (v5) — 다중 루트 워크스페이스(최대 MaxWorkspaces). MaxTokens 설정은 제거됨(서버 제어, 와이어 상수 전송).
    public const int MaxWorkspaces = 10;
    public List<WorkspaceFolder> Workspaces { get; set; } = [];

    // 신규 (Phase D)
    public string UserDisplayName { get; set; } = "";                       // empty => Environment.UserName fallback (F)

    // 신규 (UX 6종) — 모두 기본값 보유 → 스키마 호환(SchemaVersion bump 불필요)
    public string QuotaChipWindow { get; set; } = "auto";   // "auto"|"day"|"week"|"month" — 상단 쿼터 칩 표시 기준
    public bool SidebarCollapsed { get; set; } = false;     // 좌측 사이드바 접힘 상태(세션 간 유지)
    public double UiScale { get; set; } = 1.0;              // 0.9~1.6 — UI 전체 배율(접근성)

    // 확장 사고 표시. 기본 false — 서버 기본 모델(3.5-sonnet)은 확장 사고를 지원하지 않으므로
    // 켜면 그 모델에선 400 이 난다. 사고 지원 모델(4.x/5)로 설정된 경우에만 켠다.
    public bool ShowThinking { get; set; } = false;
}
