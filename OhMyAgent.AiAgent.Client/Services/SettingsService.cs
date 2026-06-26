using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using Application = System.Windows.Application;

namespace OhMyAgent.AiAgent.Client.Services;

public class SettingsService : ISettingsService
{
    /// <summary>
    /// 영속용 System.Text.Json 옵션. 에이전트 와이어용 <see cref="AgentJson.Options"/> 와는
    /// 의도적으로 분리한다.
    /// 디스크 호환성: 기존 Newtonsoft 직렬화와 동일한 형태를 보장해야 한다.
    ///  - PropertyNamingPolicy = null  → PascalCase 프로퍼티명 유지(Newtonsoft 기본과 동일)
    ///  - WriteIndented = true         → Newtonsoft Formatting.Indented(2-space) 대응
    ///  - enum 은 숫자로 직렬화(STJ/Newtonsoft 공통 기본) → Modifiers/PermissionMode 정수 유지
    ///  - PropertyNameCaseInsensitive  → 구파일 로드 견고성
    ///  - ReadCommentHandling/AllowTrailingCommas → 손상 내성(읽기)
    /// </summary>
    internal static readonly JsonSerializerOptions PersistenceOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OhMyAgent");

    private static readonly string SettingsFilePath =
        Path.Combine(SettingsDirectory, "settings.json");

    private readonly object _ioLock = new();

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync()
    {
        var migrationNeeded = await Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    if (!File.Exists(SettingsFilePath))
                    {
                        Current = new AppSettings();
                        return false;
                    }

                    var json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, PersistenceOptions);
                    Current = loaded ?? new AppSettings();

                    // 스키마 마이그레이션 — 누적형: 각 버전 블록은 return하지 않고 fall-through하며
                    // migrated 플래그만 세운다. 마지막에 한 번만 반환해 모든 버전 블록이 순차 적용된다.
                    var migrated = false;

                    // v2 -> v3: MCP 필드 drop, Phase 1 필드 기본값.
                    // McpPort/McpEnabled 는 AppSettings 에서 제거되어 역직렬화 시 자동 무시된다.
                    if (Current.SchemaVersion < 3)
                    {
                        if (string.IsNullOrEmpty(Current.ServerBaseUrl))
                            Current.ServerBaseUrl = "http://localhost:8080";
                        if (Current.MaxIterations <= 0)
                            Current.MaxIterations = 25;
                        if (string.IsNullOrEmpty(Current.AuthScheme))
                            Current.AuthScheme = "Bearer";
                        // ModelId 기본값 시드 제거: 빈 문자열 유지 → /models 에서 선택 유도.
                        Current.SchemaVersion = 3;
                        migrated = true;
                    }

                    // v3 -> v4: Phase D 필드 기본값 시드(RecentWorkspaces, UserDisplayName).
                    if (Current.SchemaVersion < 4)
                    {
                        Current.RecentWorkspaces ??= [];
                        Current.UserDisplayName ??= "";   // 빈 값 유지 → VM에서 Environment.UserName 폴백
                        Current.SchemaVersion = 4;
                        migrated = true;
                    }

                    // v4 -> v5: MaxTokens 설정 제거(서버 제어). 다중 루트 워크스페이스 도입 —
                    // 기존 단일 WorkspaceRoot 를 Workspaces[0] 으로 승격.
                    if (Current.SchemaVersion < 5)
                    {
                        Current.Workspaces ??= [];
                        if (Current.Workspaces.Count == 0 && !string.IsNullOrWhiteSpace(Current.WorkspaceRoot))
                            Current.Workspaces = [ new WorkspaceFolder { Path = Current.WorkspaceRoot, Enabled = true } ];
                        Current.SchemaVersion = 5;
                        migrated = true;
                    }

                    return migrated;
                }
                catch (Exception ex)
                {
                    // 파일 손상/파싱 실패 시 기본값으로 폴백
                    Debug.WriteLine($"[SettingsService] LoadAsync failed: {ex.Message}");
                    Current = new AppSettings();
                    return false;
                }
            }
        }).ConfigureAwait(false);

        if (migrationNeeded)
            await SaveAsync().ConfigureAwait(false); // 마이그레이션 결과 저장
    }

    public Task SaveAsync()
    {
        return Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    if (!Directory.Exists(SettingsDirectory))
                        Directory.CreateDirectory(SettingsDirectory);

                    var json = JsonSerializer.Serialize(Current, PersistenceOptions);
                    File.WriteAllText(SettingsFilePath, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SettingsService] SaveAsync failed: {ex.Message}");
                }
            }
        });
    }

    public async Task UpdateHotkeyAsync(HotkeySettings hotkey)
    {
        Current.Hotkey = hotkey;
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateOpacityAsync(double opacity)
    {
        Current.Opacity = opacity;
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateWorkspaceRootAsync(string path)
    {
        Current.WorkspaceRoot = path ?? "";
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdatePermissionModeAsync(PermissionMode mode)
    {
        Current.PermissionMode = mode;
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateServerConfigAsync(string baseUrl, string scheme, string token, string modelId, int maxIterations)
    {
        Current.ServerBaseUrl  = baseUrl ?? "";
        Current.AuthScheme     = scheme  ?? "Bearer";
        Current.AuthToken      = token   ?? "";
        Current.ModelId        = modelId ?? "";
        Current.MaxIterations  = maxIterations;
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateWorkspacesAsync(IReadOnlyList<WorkspaceFolder> folders)
    {
        var list = (folders ?? []).Take(AppSettings.MaxWorkspaces).ToList();
        Current.Workspaces = list;
        Current.WorkspaceRoot = list.FirstOrDefault(w => w.Enabled)?.Path ?? "";
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateUserDisplayNameAsync(string name)
    {
        Current.UserDisplayName = name ?? "";
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateRecentWorkspacesAsync(IReadOnlyList<WorkspaceHistoryEntry> entries)
    {
        Current.RecentWorkspaces = entries is null ? [] : entries.ToList();
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    private void RaiseSettingsChanged()
    {
        var handler = SettingsChanged;
        if (handler == null) return;

        var snapshot = Current;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            handler.Invoke(this, snapshot);
        }
        else
        {
            dispatcher.Invoke(() => handler.Invoke(this, snapshot));
        }
    }
}
