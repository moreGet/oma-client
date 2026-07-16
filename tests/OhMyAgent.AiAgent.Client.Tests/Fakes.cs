using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>인메모리 설정 스텁. WorkspaceContext 생성자 요구를 채우는 용도.</summary>
internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;

    public Task UpdateHotkeyAsync(HotkeySettings hotkey) { Current.Hotkey = hotkey; return Raise(); }
    public Task UpdateOpacityAsync(double opacity) { Current.Opacity = opacity; return Raise(); }
    public Task UpdateWorkspaceRootAsync(string path) { Current.WorkspaceRoot = path; return Raise(); }
    public Task UpdatePermissionModeAsync(PermissionMode mode) { Current.PermissionMode = mode; return Raise(); }
    public Task UpdateUserDisplayNameAsync(string name) { Current.UserDisplayName = name; return Raise(); }
    public Task UpdateQuotaChipWindowAsync(string window) { Current.QuotaChipWindow = window; return Raise(); }
    public Task UpdateSidebarCollapsedAsync(bool collapsed) { Current.SidebarCollapsed = collapsed; return Raise(); }
    public Task UpdateUiScaleAsync(double scale) { Current.UiScale = scale; return Raise(); }

    public Task UpdateWorkspacesAsync(IReadOnlyList<WorkspaceFolder> folders)
    {
        Current.Workspaces = [.. folders];
        return Raise();
    }

    public Task UpdateServerConfigAsync(string baseUrl, string scheme, string token, string modelId, int maxIterations)
    {
        Current.ServerBaseUrl = baseUrl;
        Current.AuthScheme = scheme;
        Current.AuthToken = token;
        Current.ModelId = modelId;
        Current.MaxIterations = maxIterations;
        return Raise();
    }

    private Task Raise()
    {
        SettingsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 도구 정책 조회만 구현한 API 스텁. 나머지 멤버는 호출되면 테스트를 실패시킨다 —
/// 조용히 기본값을 돌려주면 테스트가 의도치 않은 경로를 통과해도 눈치채지 못한다.
/// </summary>
internal sealed class FakePolicyApiClient(Func<ToolPolicyFetch> policy) : IAgentApiClient
{
    /// <summary>GetToolPolicyAsync 호출 횟수 — 재시도 동작 검증용.</summary>
    public int PolicyCallCount { get; private set; }

    /// <summary>AuthorizeToolAsync 가 돌려줄 값(realtime 모드 테스트용).</summary>
    public ToolAuthorization? Authorization { get; set; }

    public Task<ToolPolicyFetch> GetToolPolicyAsync(CancellationToken ct = default)
    {
        PolicyCallCount++;
        return Task.FromResult(policy());
    }

    public Task<ToolAuthorization?> AuthorizeToolAsync(string tool, JsonElement arguments, CancellationToken ct = default)
        => Task.FromResult(Authorization);

    private static T NotUsed<T>() => throw new NotSupportedException("이 테스트에서 호출될 리 없는 멤버입니다.");

    public IAsyncEnumerable<AgentStreamEvent> SendAsync(AgentRequest request, CancellationToken ct = default) => NotUsed<IAsyncEnumerable<AgentStreamEvent>>();
    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => NotUsed<Task<bool>>();
    public Task<ServerReadiness> CheckReadinessAsync(CancellationToken ct = default) => NotUsed<Task<ServerReadiness>>();
    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default) => NotUsed<Task<IReadOnlyList<ModelInfo>>>();
    public Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default) => NotUsed<Task<LoginResult>>();
    public Task<UserProfile?> GetProfileAsync(CancellationToken ct = default) => NotUsed<Task<UserProfile?>>();
    public Task<ClientVersionInfo?> GetClientVersionAsync(CancellationToken ct = default) => NotUsed<Task<ClientVersionInfo?>>();
    public Task<QuotaResponse?> GetQuotaAsync(CancellationToken ct = default) => NotUsed<Task<QuotaResponse?>>();
    public Task<CommandSecurityPolicyResponse?> GetCommandSecurityPolicyAsync(CancellationToken ct = default) => NotUsed<Task<CommandSecurityPolicyResponse?>>();
    public Task<IReadOnlyList<RemoteProject>> ListRemoteProjectsAsync(CancellationToken ct = default) => NotUsed<Task<IReadOnlyList<RemoteProject>>>();
    public Task<RemoteProject> UpsertRemoteProjectAsync(RemoteProjectUpsert body, CancellationToken ct = default) => NotUsed<Task<RemoteProject>>();
    public Task UpsertRemoteConversationAsync(string remoteProjectId, RemoteConversation body, CancellationToken ct = default) => NotUsed<Task>();
    public Task DeleteRemoteProjectAsync(string remoteProjectId, CancellationToken ct = default) => NotUsed<Task>();
    public Task DeleteRemoteConversationAsync(string remoteProjectId, string remoteConversationId, CancellationToken ct = default) => NotUsed<Task>();
    public Task<IReadOnlyList<RemoteSessionSummary>?> ListRemoteSessionsAsync(CancellationToken ct = default) => NotUsed<Task<IReadOnlyList<RemoteSessionSummary>?>>();
    public Task<RemoteSession?> GetRemoteSessionAsync(string id, CancellationToken ct = default) => NotUsed<Task<RemoteSession?>>();
    public Task<bool> PutRemoteSessionAsync(string id, string title, JsonElement data, CancellationToken ct = default) => NotUsed<Task<bool>>();
    public Task DeleteRemoteSessionAsync(string id, CancellationToken ct = default) => NotUsed<Task>();
}
