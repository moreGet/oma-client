using CommunityToolkit.Mvvm.ComponentModel;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 다중 루트 워크스페이스의 한 행. Path는 읽기 전용, Enabled(접근 토글)는 TwoWay.
/// Enabled 변경 시 부모(SettingsViewModel)가 등록한 콜백으로 영속을 트리거한다.
/// </summary>
public partial class WorkspaceFolderViewModel : ObservableObject
{
    private readonly System.Action<WorkspaceFolderViewModel>? _onEnabledChanged;

    /// <summary>폴더 절대 경로(읽기 전용, UI에서 말줄임·ToolTip 표시).</summary>
    public string Path { get; }

    /// <summary>접근 허용/차단 토글. 변경 시 부모 콜백으로 영속.</summary>
    [ObservableProperty] private bool _enabled;

    public WorkspaceFolderViewModel(
        string path,
        bool enabled,
        System.Action<WorkspaceFolderViewModel>? onEnabledChanged = null)
    {
        Path = path;
        _enabled = enabled;
        _onEnabledChanged = onEnabledChanged;
    }

    partial void OnEnabledChanged(bool value) => _onEnabledChanged?.Invoke(this);
}
