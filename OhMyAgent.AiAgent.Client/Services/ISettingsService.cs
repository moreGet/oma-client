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
}
