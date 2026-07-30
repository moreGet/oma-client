using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

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

    private readonly IUiDispatcher _ui;

    /// <summary>UI 스레드 마샬링 주입. WPF 호스트는 <c>WpfUiDispatcher</c>, 헤드리스는 <c>ImmediateUiDispatcher</c>.</summary>
    public SettingsService(IUiDispatcher ui) => _ui = ui;

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

                    // v3 -> v4: Phase D 필드 기본값 시드(UserDisplayName).
                    if (Current.SchemaVersion < 4)
                    {
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

                    // 토큰 복호화: v6 이상은 AuthTokenProtected(DPAPI), v5 이하는 평문 AuthToken.
                    Current.AuthToken = TokenProtector.Unprotect(Current.AuthTokenProtected);

                    // v5 -> v6: 평문 AuthToken 을 DPAPI 로 옮긴다.
                    if (Current.SchemaVersion < 6)
                    {
                        if (!string.IsNullOrEmpty(Current.LegacyAuthToken))
                        {
                            Current.AuthToken = Current.LegacyAuthToken;
                            AppLog.Info("SettingsService", "평문 인증 토큰을 발견해 DPAPI 로 이전합니다(v6).");
                        }
                        Current.SchemaVersion = 6;
                        migrated = true;   // 아래에서 SaveAsync → 암호문으로 다시 기록됨
                    }

                    // 평문 키 제거는 버전 분기 "밖" 이어야 한다.
                    // 안에 두면 SchemaVersion 이 이미 6 인데 평문 AuthToken 키가 남아 있는 파일
                    // (손으로 편집·백업 복원·다운그레이드 후 재실행)에서 LegacyAuthToken 이 non-null 로 남고,
                    // 다음 SaveAsync 가 그대로 평문으로 다시 기록한다.
                    if (Current.LegacyAuthToken is not null)
                    {
                        Current.LegacyAuthToken = null;
                        migrated = true;   // 평문 키가 파일에서 사라지도록 저장을 유도
                    }

                    return migrated;
                }
                catch (Exception ex)
                {
                    // 파일 손상/파싱 실패 → 기본값으로 폴백하되, 원본은 버리지 않고 백업해 둔다.
                    // 종전에는 조용히 버려서 토큰·워크스페이스·단축키가 통째로 사라졌다.
                    AppLog.Warn("SettingsService", "LoadAsync failed", ex);
                    BackupCorruptFile();
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

                    // 평문 토큰은 [JsonIgnore] 라 나가지 않는다. 저장 직전에 암호문만 채운다.
                    // 암호화 실패 시 Protect 가 null 을 반환하고, 그러면 토큰 없이 저장된다(평문 폴백 금지).
                    Current.AuthTokenProtected = TokenProtector.Protect(Current.AuthToken);

                    var json = JsonSerializer.Serialize(Current, PersistenceOptions);

                    // 원자적 교체: 종전의 단순 WriteAllText 는 쓰기 중 크래시/전원차단 시
                    // 잘린 JSON 을 남겼고, 다음 로드가 그걸 파싱 실패로 보고 설정을 통째로 초기화했다.
                    // ChatHistoryService 가 이미 쓰던 패턴과 동일하게 맞춘다.
                    var tmp = SettingsFilePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, SettingsFilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("SettingsService", "SaveAsync failed", ex);
                }
            }
        });
    }

    /// <summary>손상된 settings.json 을 .corrupt 로 옮겨 둔다(덮어쓰기 전에 원본 보존).</summary>
    private static void BackupCorruptFile()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;

            var backup = SettingsFilePath + ".corrupt";
            File.Move(SettingsFilePath, backup, overwrite: true);
            AppLog.Warn("SettingsService", $"손상된 설정 파일을 백업했습니다: {backup}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("SettingsService", "손상 파일 백업 실패", ex);
        }
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

    public async Task UpdateQuotaChipWindowAsync(string window)
    {
        var normalized = (window ?? "").Trim().ToLowerInvariant();
        Current.QuotaChipWindow = normalized switch
        {
            "auto" or "day" or "week" or "month" => normalized,
            _                                    => "auto",
        };
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateSidebarCollapsedAsync(bool collapsed)
    {
        Current.SidebarCollapsed = collapsed;
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateUiScaleAsync(double scale)
    {
        Current.UiScale = Math.Clamp(scale, 0.9, 1.6);
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    public async Task UpdateAlwaysOnTopAsync(bool alwaysOnTop)
    {
        Current.AlwaysOnTop = alwaysOnTop;
        await SaveAsync().ConfigureAwait(false);
        RaiseSettingsChanged();
    }

    private void RaiseSettingsChanged()
    {
        var handler = SettingsChanged;
        if (handler == null) return;

        var snapshot = Current;
        if (_ui.CheckAccess())
        {
            handler.Invoke(this, snapshot);
        }
        else
        {
            _ui.Invoke(() => handler.Invoke(this, snapshot));
        }
    }
}
