using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    Task LoadAsync();
    Task SaveAsync();
    Task UpdateHotkeyAsync(HotkeySettings hotkey);
    Task UpdateOpacityAsync(double opacity);

    // Phase 1
    Task UpdateWorkspaceRootAsync(string path);
    Task UpdatePermissionModeAsync(PermissionMode mode);
    Task UpdateServerConfigAsync(string baseUrl, string scheme, string token, string modelId, int maxIterations);

    // v5 — 다중 루트 워크스페이스(최대 AppSettings.MaxWorkspaces). 첫 Enabled Path 를 WorkspaceRoot(주 루트)로 동기화.
    Task UpdateWorkspacesAsync(IReadOnlyList<WorkspaceFolder> folders);

    // Phase D
    Task UpdateUserDisplayNameAsync(string name);                                    // F

    // UX 6종 — 상단바/사이드바/접근성
    Task UpdateQuotaChipWindowAsync(string window);   // 소문자화 후 {auto,day,week,month} 외면 "auto"로 보정
    Task UpdateSidebarCollapsedAsync(bool collapsed);
    Task UpdateUiScaleAsync(double scale);            // Math.Clamp(scale, 0.9, 1.6)

    // 창 상태 — 메인 창 최상위 고정(상단바 핀 토글). 세션 간 유지가 요구사항이라 즉시 저장한다.
    Task UpdateAlwaysOnTopAsync(bool alwaysOnTop);
}
