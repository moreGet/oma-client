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
    [ObservableProperty] private string _authScheme = "Bearer";
    [ObservableProperty] private string _authToken = string.Empty;
    [ObservableProperty] private string _modelId = string.Empty;

    public IReadOnlyList<string> AuthSchemes { get; } = ["Bearer", "ApiKey"];

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
        AuthScheme = c.AuthScheme;
        AuthToken = c.AuthToken;
        ModelId = c.ModelId;
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
            ServerBaseUrl, AuthScheme, AuthToken, ModelId, MaxIterations, MaxTokens);
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
