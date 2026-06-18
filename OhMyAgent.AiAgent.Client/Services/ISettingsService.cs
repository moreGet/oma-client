using System;
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
    Task UpdateServerConfigAsync(string baseUrl, string scheme, string token, string modelId, int maxIterations, int maxTokens);
}
