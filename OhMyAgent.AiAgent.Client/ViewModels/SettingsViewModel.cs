using System.Collections.ObjectModel;
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

    // ── Hotkey capture (existing) ──────────────────────────────────────
    [ObservableProperty] private HotkeyModifiers _modifiers;
    [ObservableProperty] private System.Windows.Input.Key _key;
    [ObservableProperty] private string _displayText = string.Empty;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string? _validationError;

    // ── Workspace ──────────────────────────────────────────────────────
    [ObservableProperty] private string _workspaceRoot = string.Empty;

    // ── User profile (F) ───────────────────────────────────────────────
    [ObservableProperty] private string _userDisplayName = string.Empty;

    // ── Permission mode ────────────────────────────────────────────────
    [ObservableProperty] private PermissionMode _permissionMode = PermissionMode.Manual;

    public IReadOnlyList<PermissionMode> PermissionModes { get; } =
        [PermissionMode.Manual, PermissionMode.AutoSafe, PermissionMode.FullAuto];

    /// <summary>Warning shown when Full-Auto is selected (all tools auto-approved).</summary>
    public bool ShowFullAutoWarning => PermissionMode == PermissionMode.FullAuto;

    // ── Iteration / token budget ───────────────────────────────────────
    [ObservableProperty] private int _maxIterations = 25;
    [ObservableProperty] private int _maxTokens = 4096;

    // ── Server config ──────────────────────────────────────────────────
    [ObservableProperty] private string _serverBaseUrl = "http://localhost:8080";
    [ObservableProperty] private string _authToken = string.Empty;
    [ObservableProperty] private string _modelId = string.Empty;

    // ── Login (JWT Bearer) ─────────────────────────────────────────────
    [ObservableProperty] private string _username = string.Empty;

    /// <summary>
    /// 로그인 비밀번호. PasswordBox는 바인딩 불가하므로 View 코드비하인드에서 푸시한다
    /// (기존 AuthTokenBox 패턴과 동일).
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>로그인 진행/결과 상태 메시지. ("로그인됨" / "로그인 중..." / "실패: ...")</summary>
    [ObservableProperty] private string _loginStatus = string.Empty;

    /// <summary>토큰 보유 여부 (UI 게이팅용).</summary>
    [ObservableProperty] private bool _isLoggedIn;

    /// <summary>Model ids fetched from the server (free-text fallback in the View).</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    public SettingsViewModel(ISettingsService settings, IAgentApiClient api)
    {
        _settings = settings;
        _api = api;

        var c = settings.Current;
        Modifiers = c.Hotkey.Modifiers;
        Key = (System.Windows.Input.Key)c.Hotkey.KeyCode;
        DisplayText = c.Hotkey.ToDisplayString();

        WorkspaceRoot = c.WorkspaceRoot;
        UserDisplayName = c.UserDisplayName;
        PermissionMode = c.PermissionMode;
        MaxIterations = c.MaxIterations;
        MaxTokens = c.MaxTokens;
        ServerBaseUrl = c.ServerBaseUrl;
        AuthToken = c.AuthToken;
        ModelId = c.ModelId;

        // 저장된 토큰이 있으면 로그인 상태로 초기화 (별도 초기화 호출 없이 생성자에서).
        IsLoggedIn = !string.IsNullOrWhiteSpace(AuthToken);
        LoginStatus = IsLoggedIn ? "로그인됨" : string.Empty;
    }

    public async Task InitializeAsync()
    {
        await LoadModelsAsync();
    }

    // ── Workspace picker ───────────────────────────────────────────────

    /// <summary>
    /// Called by the View after a folder dialog returns a path. Persists immediately.
    /// </summary>
    public async Task SetWorkspaceRootAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        WorkspaceRoot = path;
        await _settings.UpdateWorkspaceRootAsync(path);
    }

    partial void OnPermissionModeChanged(PermissionMode value)
    {
        OnPropertyChanged(nameof(ShowFullAutoWarning));
        _ = _settings.UpdatePermissionModeAsync(value);
    }

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
            ServerBaseUrl, "Bearer", AuthToken, ModelId, MaxIterations, MaxTokens);
    }

    // ── Login (JWT Bearer) ─────────────────────────────────────────────

    /// <summary>
    /// POST /api/v1/auth/login → 성공 시 JWT를 AuthToken에 담아 단일 영속 경로
    /// (UpdateServerConfigAsync)로 저장. 실패 시 상태 메시지로 노출.
    /// </summary>
    [RelayCommand]
    private async Task LoginAsync()
    {
        LoginStatus = "로그인 중...";

        var result = await _api.LoginAsync(Username, Password);
        if (result.Success)
        {
            AuthToken = result.Token!;
            await _settings.UpdateServerConfigAsync(
                ServerBaseUrl, "Bearer", AuthToken, ModelId, MaxIterations, MaxTokens);
            IsLoggedIn = true;
            LoginStatus = "로그인됨";
            Password = string.Empty;
        }
        else
        {
            IsLoggedIn = false;
            LoginStatus = $"실패: {result.ErrorMessage}";
        }
    }

    // ── User profile (F) ───────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveUserProfileAsync()
    {
        await _settings.UpdateUserDisplayNameAsync(UserDisplayName);
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
}
