using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Client.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    [ObservableProperty] private HotkeyModifiers _modifiers;
    [ObservableProperty] private System.Windows.Input.Key _key;
    [ObservableProperty] private string _displayText = string.Empty;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string? _validationError;

    public SettingsViewModel(ISettingsService settings)
    {
        _settings   = settings;
        Modifiers   = settings.Current.Hotkey.Modifiers;
        Key         = (System.Windows.Input.Key)settings.Current.Hotkey.KeyCode;
        DisplayText = settings.Current.Hotkey.ToDisplayString();
    }

    partial void OnIsCapturingChanged(bool value)
        => SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void StartCapture()
    {
        IsCapturing     = true;
        ValidationError = null;
    }

    [RelayCommand]
    private void CancelCapture()
    {
        IsCapturing = false;

        // 현재 저장된 값으로 복원
        Modifiers       = _settings.Current.Hotkey.Modifiers;
        Key             = (System.Windows.Input.Key)_settings.Current.Hotkey.KeyCode;
        DisplayText     = _settings.Current.Hotkey.ToDisplayString();
        ValidationError = null;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        await _settings.UpdateHotkeyAsync(new HotkeySettings
        {
            Modifiers = Modifiers,
            KeyCode   = (int)Key,
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

        // Esc → 캡처 취소
        if (key == System.Windows.Input.Key.Escape) { CancelCapture(); return; }

        // 단독 수정키는 무시
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

        Modifiers       = newMods;
        Key             = key;
        DisplayText     = new HotkeySettings { Modifiers = newMods, KeyCode = (int)key }.ToDisplayString();
        ValidationError = null;
        IsCapturing     = false;
        SaveCommand.NotifyCanExecuteChanged();
    }
}
