using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Client.ViewModels;

/// <summary>
/// 첫 랜딩(로그인) 페이지 ViewModel. 로그인 성공 시에만 <see cref="Succeeded"/> 를 올려
/// 호스트(App)가 메인으로 진입하도록 한다.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IAgentApiClient _api;
    private readonly ISettingsService _settings;

    public LoginViewModel(IAgentApiClient api, ISettingsService settings)
    {
        _api = api;
        _settings = settings;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _username = string.Empty;

    /// <summary>PasswordBox 는 바인딩 불가 — 코드비하인드에서 푸시.</summary>
    public string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // ── 서버 연결 상태 (좌상단 표시 + 새로고침) ──
    [ObservableProperty] private bool _serverOnline;
    [ObservableProperty] private string _serverStatusText = "서버 확인 중...";

    /// <summary>로그인 성공.</summary>
    public event EventHandler? Succeeded;

    /// <summary>/health 로 서버 연결 상태를 갱신한다(좌상단 표시용).</summary>
    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        ServerStatusText = "서버 확인 중...";
        bool ok;
        try { ok = await _api.CheckHealthAsync().ConfigureAwait(true); }
        catch { ok = false; }

        ServerOnline = ok;
        ServerStatusText = ok ? "서버 연결됨" : "서버 연결 안됨";
    }

    private bool CanLogin() => !IsBusy && !string.IsNullOrWhiteSpace(Username);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        IsBusy = true;
        HasError = false;
        StatusMessage = "로그인 중...";
        try
        {
            var result = await _api.LoginAsync(Username.Trim(), Password).ConfigureAwait(true);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Token))
            {
                HasError = true;
                StatusMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "로그인에 실패했습니다. 아이디와 비밀번호를 확인하세요."
                    : result.ErrorMessage!;
                return;
            }

            // 발급 토큰을 영속(Bearer 고정). 다른 서버 설정은 유지.
            var c = _settings.Current;
            await _settings.UpdateServerConfigAsync(
                c.ServerBaseUrl, "Bearer", result.Token, c.ModelId, c.MaxIterations)
                .ConfigureAwait(true);

            Password = string.Empty;
            StatusMessage = "로그인 성공";
            Succeeded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"연결 오류: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
