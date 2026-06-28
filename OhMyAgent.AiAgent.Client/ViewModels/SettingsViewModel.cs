using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Client.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IAgentApiClient _api;

    // Guards re-entrant persistence while (re)building the Workspaces collection.
    private bool _suppressWorkspacePersist;

    // ── Hotkey capture (existing) ──────────────────────────────────────
    [ObservableProperty] private HotkeyModifiers _modifiers;
    [ObservableProperty] private System.Windows.Input.Key _key;
    [ObservableProperty] private string _displayText = string.Empty;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string? _validationError;

    // ── Workspace (primary root, display only) ─────────────────────────
    [ObservableProperty] private string _workspaceRoot = string.Empty;

    // ── Multi-root workspaces (#3) ─────────────────────────────────────

    /// <summary>다중 루트 워크스페이스 행. 각 항목: Path(읽기), Enabled(TwoWay), 삭제는 RemoveWorkspaceCommand.</summary>
    public ObservableCollection<WorkspaceFolderViewModel> Workspaces { get; } = [];

    /// <summary>최대 개수(10) 도달 시 Add 비활성·경고 표시용.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWorkspaceCommand))]
    private bool _canAddWorkspace = true;

    /// <summary>워크스페이스가 하나도 없을 때 경고 표시용.</summary>
    public bool HasNoWorkspaces => Workspaces.Count == 0;

    /// <summary>최대 워크스페이스 개수(View 안내 문구용).</summary>
    public int MaxWorkspaces => AppSettings.MaxWorkspaces;

    // ── User profile (#5 — server-fetched, read-only) ──────────────────
    [ObservableProperty] private string _profileDisplayName = string.Empty;
    [ObservableProperty] private string _profileUsername = string.Empty;
    [ObservableProperty] private string _profileOrganization = string.Empty;
    [ObservableProperty] private string _profileEmail = string.Empty;

    /// <summary>프로필 로딩/실패 상태 안내. ("불러오는 중..." / "프로필을 불러오지 못했습니다" / "")</summary>
    [ObservableProperty] private string _profileStatus = string.Empty;

    // ── Permission mode ────────────────────────────────────────────────
    [ObservableProperty] private PermissionMode _permissionMode = PermissionMode.Manual;

    public IReadOnlyList<PermissionMode> PermissionModes { get; } =
        [PermissionMode.Manual, PermissionMode.AutoSafe, PermissionMode.FullAuto];

    /// <summary>Warning shown when Full-Auto is selected (all tools auto-approved).</summary>
    public bool ShowFullAutoWarning => PermissionMode == PermissionMode.FullAuto;

    // ── Iteration budget ───────────────────────────────────────────────
    [ObservableProperty] private int _maxIterations = 25;

    // ── UI scale (accessibility) ───────────────────────────────────────

    /// <summary>UI 전체 배율(0.9~1.6). 슬라이더 바인딩 → 변경 시 즉시 영속. 설정창 미리보기도 이 값에 바인딩.</summary>
    [ObservableProperty] private double _uiScale = 1.0;

    // ── Server config ──────────────────────────────────────────────────
    [ObservableProperty] private string _serverBaseUrl = "http://localhost:8080";
    [ObservableProperty] private string _authToken = string.Empty;
    [ObservableProperty] private string _modelId = string.Empty;

    // ── Login status (read-only; login itself happens at the gate) ─────

    /// <summary>로그인 진행/결과 상태 메시지. ("로그인됨" / "로그아웃됨")</summary>
    [ObservableProperty] private string _loginStatus = string.Empty;

    /// <summary>토큰 보유 여부 (UI 게이팅용).</summary>
    [ObservableProperty] private bool _isLoggedIn;

    /// <summary>Model ids fetched from the server (free-text fallback in the View).</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    /// <summary>앱 버전 표시(빌드 해시 포함). 예: "v1.3.0+abc1234".</summary>
    public string AppVersionText => $"v{AppVersion.Full}";

    public SettingsViewModel(ISettingsService settings, IAgentApiClient api)
    {
        _settings = settings;
        _api = api;

        var c = settings.Current;
        Modifiers = c.Hotkey.Modifiers;
        Key = (System.Windows.Input.Key)c.Hotkey.KeyCode;
        DisplayText = c.Hotkey.ToDisplayString();

        WorkspaceRoot = c.WorkspaceRoot;
        PermissionMode = c.PermissionMode;
        MaxIterations = c.MaxIterations;
        ServerBaseUrl = c.ServerBaseUrl;
        AuthToken = c.AuthToken;
        ModelId = c.ModelId;
        UiScale = c.UiScale;

        // 다중 루트 워크스페이스 초기화 (설정에서 복원).
        LoadWorkspaces(c.Workspaces);

        // 저장된 토큰이 있으면 로그인 상태로 초기화 (별도 초기화 호출 없이 생성자에서).
        IsLoggedIn = !string.IsNullOrWhiteSpace(AuthToken);
        LoginStatus = IsLoggedIn ? "로그인됨" : string.Empty;

        // 프로필 폴백 — 서버 조회 전 OS 사용자명을 우선 표시.
        ProfileDisplayName = Environment.UserName;
    }

    public async Task InitializeAsync()
    {
        await LoadModelsAsync();
        await LoadProfileAsync();
    }

    // ── Workspace list (#3) ────────────────────────────────────────────

    private void LoadWorkspaces(IReadOnlyList<WorkspaceFolder> folders)
    {
        _suppressWorkspacePersist = true;
        try
        {
            Workspaces.Clear();
            foreach (var f in folders)
                Workspaces.Add(new WorkspaceFolderViewModel(f.Path, f.Enabled, OnWorkspaceEnabledChanged));
        }
        finally
        {
            _suppressWorkspacePersist = false;
        }
        RefreshWorkspaceState();
    }

    /// <summary>
    /// View 코드비하인드가 폴더 다이얼로그로 받은 경로로 호출. 중복·상한 차단 후 추가·영속.
    /// </summary>
    public async Task AddWorkspaceByPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Workspaces.Count >= AppSettings.MaxWorkspaces) return;

        // 중복 차단(대소문자·말미 구분자 무시).
        var normalized = path.TrimEnd('\\', '/');
        if (Workspaces.Any(w =>
                string.Equals(w.Path.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        Workspaces.Add(new WorkspaceFolderViewModel(path, enabled: true, OnWorkspaceEnabledChanged));
        RefreshWorkspaceState();
        await PersistWorkspacesAsync().ConfigureAwait(false);
    }

    private bool CanAddWorkspaceExecute() => CanAddWorkspace;

    /// <summary>
    /// 폴더 선택은 View 코드비하인드 책임. 이 커맨드는 상한 게이트만 담당하며,
    /// View가 다이얼로그를 띄운 뒤 <see cref="AddWorkspaceByPathAsync"/>를 호출한다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddWorkspaceExecute))]
    private void AddWorkspace()
    {
        // No-op: dialog is owned by the View (MVVM-safe). See AddWorkspaceByPathAsync.
    }

    private bool CanRemoveWorkspace(WorkspaceFolderViewModel? item) => item is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveWorkspace))]
    private async Task RemoveWorkspaceAsync(WorkspaceFolderViewModel? item)
    {
        if (item is null) return;
        if (!Workspaces.Remove(item)) return;
        RefreshWorkspaceState();
        await PersistWorkspacesAsync().ConfigureAwait(false);
    }

    private void OnWorkspaceEnabledChanged(WorkspaceFolderViewModel item)
    {
        if (_suppressWorkspacePersist) return;
        _ = PersistWorkspacesAsync();
    }

    private async Task PersistWorkspacesAsync()
    {
        var folders = Workspaces
            .Select(w => new WorkspaceFolder { Path = w.Path, Enabled = w.Enabled })
            .ToList();
        await _settings.UpdateWorkspacesAsync(folders).ConfigureAwait(false);

        // 주 루트(첫 활성) 표시 동기화.
        var primary = folders.FirstOrDefault(f => f.Enabled)?.Path
                      ?? folders.FirstOrDefault()?.Path
                      ?? string.Empty;
        await UiInvokeAsync(() => WorkspaceRoot = primary).ConfigureAwait(false);
    }

    private void RefreshWorkspaceState()
    {
        CanAddWorkspace = Workspaces.Count < AppSettings.MaxWorkspaces;
        OnPropertyChanged(nameof(HasNoWorkspaces));
    }

    partial void OnPermissionModeChanged(PermissionMode value)
    {
        OnPropertyChanged(nameof(ShowFullAutoWarning));
        _ = _settings.UpdatePermissionModeAsync(value);
    }

    // 즉시 영속(다른 필드와 동일 패턴 — 별도 Seed 역기록 가드 없음). 서비스가 0.9~1.6로 클램프한다.
    partial void OnUiScaleChanged(double value)
        => _ = _settings.UpdateUiScaleAsync(value);

    // ── Server config ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        try
        {
            var models = await _api.GetModelsAsync();
            AvailableModels.Clear();
            foreach (var m in models)
                AvailableModels.Add(m.Id);
        }
        catch
        {
            // Endpoint absent / offline — free-text fallback in the View.
        }
    }

    [RelayCommand]
    private async Task SaveServerConfigAsync()
    {
        await _settings.UpdateServerConfigAsync(
            ServerBaseUrl, "Bearer", AuthToken, ModelId, MaxIterations);
    }

    // ── Logout (status + sign-out; login itself happens at the gate) ───

    /// <summary>로그아웃 완료 시 발생 — App이 받아 모든 세션/창을 강제 종료하고 로그인 화면으로 회귀시킨다.</summary>
    public event EventHandler? LoggedOut;

    /// <summary>로그아웃 — 저장된 토큰을 제거하고, 모든 세션 강제 종료 + 로그인 화면 회귀를 요청한다.</summary>
    [RelayCommand]
    private async Task LogoutAsync()
    {
        AuthToken = string.Empty;
        await _settings.UpdateServerConfigAsync(
            ServerBaseUrl, "Bearer", string.Empty, ModelId, MaxIterations);
        IsLoggedIn = false;
        LoginStatus = "로그아웃됨";

        // App이 모든 창을 닫고 로그인 랜딩으로 회귀시킨다.
        LoggedOut?.Invoke(this, EventArgs.Empty);
    }

    // ── User profile (#5 — server-fetched, read-only) ──────────────────

    /// <summary>
    /// GET /api/v1/users/me 로 프로필을 조회해 표시. 실패(null) 시 OS 사용자명으로 폴백.
    /// </summary>
    private async Task LoadProfileAsync()
    {
        ProfileStatus = "불러오는 중...";
        UserProfile? profile;
        try
        {
            profile = await _api.GetProfileAsync();
        }
        catch
        {
            profile = null;
        }

        if (profile is not null)
        {
            ProfileUsername = profile.Username;
            ProfileDisplayName = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? Environment.UserName
                : profile.DisplayName;
            ProfileOrganization = profile.Organization ?? string.Empty;
            ProfileEmail = profile.Email ?? string.Empty;
            ProfileStatus = string.Empty;
        }
        else
        {
            ProfileUsername = string.Empty;
            ProfileDisplayName = Environment.UserName;
            ProfileOrganization = string.Empty;
            ProfileEmail = string.Empty;
            ProfileStatus = "프로필을 불러오지 못했습니다";
        }
    }

    // ── Hotkey capture (existing behavior, preserved) ──────────────────

    partial void OnIsCapturingChanged(bool value)
        => SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void StartCapture()
    {
        IsCapturing = true;
        ValidationError = null;
    }

    [RelayCommand]
    private void CancelCapture()
    {
        IsCapturing = false;
        Modifiers = _settings.Current.Hotkey.Modifiers;
        Key = (System.Windows.Input.Key)_settings.Current.Hotkey.KeyCode;
        DisplayText = _settings.Current.Hotkey.ToDisplayString();
        ValidationError = null;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        await _settings.UpdateHotkeyAsync(new HotkeySettings
        {
            Modifiers = Modifiers,
            KeyCode = (int)Key,
        });
        IsCapturing = false;
    }

    private bool CanSave()
        => !IsCapturing
           && Key != System.Windows.Input.Key.None
           && Modifiers != HotkeyModifiers.None
           && ValidationError == null;

    /// <summary>
    /// View의 KeyDown 이벤트에서 호출. 캡처 중일 때만 동작.
    /// </summary>
    public void ApplyCapturedKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
    {
        if (!IsCapturing) return;

        if (key == System.Windows.Input.Key.Escape) { CancelCapture(); return; }

        if (key == System.Windows.Input.Key.LeftCtrl  || key == System.Windows.Input.Key.RightCtrl  ||
            key == System.Windows.Input.Key.LeftAlt   || key == System.Windows.Input.Key.RightAlt   ||
            key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift ||
            key == System.Windows.Input.Key.LWin      || key == System.Windows.Input.Key.RWin) return;

        var newMods = (HotkeyModifiers)0;
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) newMods |= HotkeyModifiers.Ctrl;
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt))     newMods |= HotkeyModifiers.Alt;
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift))   newMods |= HotkeyModifiers.Shift;
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Windows)) newMods |= HotkeyModifiers.Win;

        if (newMods == HotkeyModifiers.None)
        {
            ValidationError = "수정키(Ctrl/Alt/Shift)를 함께 누르세요.";
            return;
        }

        Modifiers = newMods;
        Key = key;
        DisplayText = new HotkeySettings { Modifiers = newMods, KeyCode = (int)key }.ToDisplayString();
        ValidationError = null;
        IsCapturing = false;
        SaveCommand.NotifyCanExecuteChanged();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static Task UiInvokeAsync(Action action) => UiDispatch.InvokeAsync(action);
}
