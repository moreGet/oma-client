using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Models;

public class AppSettings
{
    // 기존 — 유지
    public HotkeySettings Hotkey { get; set; } = HotkeySettings.Default;
    public double Opacity { get; set; } = 1.0;
    public int SchemaVersion { get; set; } = 4;   // bump 3 -> 4

    // MCP 서버 은퇴로 제거됨 (v3 마이그레이션에서 drop):
    //   public int  McpPort    { get; set; } = 3000;
    //   public bool McpEnabled { get; set; } = true;

    // 신규 (Phase 1)
    public string WorkspaceRoot { get; set; } = "";            // empty => Desktop fallback
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Manual;
    public int MaxIterations { get; set; } = 25;
    public string ServerBaseUrl { get; set; } = "http://localhost:8080";
    public string AuthScheme { get; set; } = "Bearer";          // "Bearer" | "ApiKey"
    public string AuthToken { get; set; } = "";
    public string ModelId { get; set; } = "";
    public int MaxTokens { get; set; } = 4096;

    // 신규 (Phase D)
    public string UserDisplayName { get; set; } = "";                       // empty => Environment.UserName fallback (F)
    public List<WorkspaceHistoryEntry> RecentWorkspaces { get; set; } = []; // 최근순, 상한 10 (B)
}
