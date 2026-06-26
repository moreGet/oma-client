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

/// <summary>
/// 프로젝트(대화 컨테이너) 사이드바 ViewModel. 로컬 우선 CRUD + 선택적 서버 동기화.
/// 세션 요약(ChatSessionSummary.ProjectId)을 프로젝트별로 group-by 해 "미분류"(가상) 그룹과
/// 함께 노출한다. AgentSessionViewModel은 변경하지 않으며, 세션 목록 스냅샷만 주입받는다.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly IProjectService _projects;
    private readonly IChatHistoryService _chatHistory;

    /// <summary>미분류 그룹의 합성 Id(실제 프로젝트가 아님; null ProjectId 세션 묶음).</summary>
    public const string UnassignedId = "__unassigned__";

    /// <summary>실제 프로젝트 + 마지막에 "미분류" 가상 항목을 포함한 사이드바 목록.</summary>
    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];

    /// <summary>상태/오류 안내 메시지(동기화 미지원 등).</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>선택된 프로젝트(편입/해제·rename·delete·sync 대상).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    private ProjectItemViewModel? _selectedProject;

    public ProjectsViewModel(IProjectService projects, IChatHistoryService chatHistory)
    {
        _projects = projects;
        _chatHistory = chatHistory;
    }

    /// <summary>프로젝트 목록 + 세션 group-by 로드. AgentSessionViewModel.InitializeAsync 직후 호출 권장.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IReadOnlyList<ProjectSummary> summaries;
        IReadOnlyList<ChatSessionSummary> sessions;
        try
        {
            summaries = await _projects.ListAsync().ConfigureAwait(false);
            sessions = await _chatHistory.ListAsync().ConfigureAwait(false);
        }
        catch
        {
            summaries = [];
            sessions = [];
        }

        var byProject = sessions
            .Where(s => s.ProjectId is not null)
            .GroupBy(s => s.ProjectId!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        await UiInvokeAsync(() =>
        {
            Projects.Clear();

            foreach (var p in summaries)
            {
                var children = byProject.TryGetValue(p.Id, out var list) ? list : [];
                Projects.Add(new ProjectItemViewModel(
                    p.Id, p.Name, children.Count, p.Synced, p.UpdatedUtc, isUnassigned: false, children));
            }

            // 미분류(가상) 그룹 — 항상 마지막.
            var unassigned = sessions.Where(s => s.ProjectId is null).ToList();
            Projects.Add(new ProjectItemViewModel(
                UnassignedId, "미분류", unassigned.Count, synced: false,
                DateTimeOffset.MinValue, isUnassigned: true, unassigned));
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task CreateProjectAsync(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) trimmed = "새 프로젝트";

        try
        {
            await _projects.CreateAsync(trimmed).ConfigureAwait(false);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"프로젝트 생성 실패: {ex.Message}";
        }

        await LoadAsync().ConfigureAwait(false);
    }

    private bool CanModifySelected()
        => SelectedProject is { IsUnassigned: false };

    /// <summary>선택 프로젝트 이름 변경. 새 이름은 인자로 전달(View 인라인 편집/다이얼로그).</summary>
    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task RenameAsync(string? newName)
    {
        if (SelectedProject is not { IsUnassigned: false } p) return;
        var trimmed = (newName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        try
        {
            await _projects.RenameAsync(p.Id, trimmed).ConfigureAwait(false);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"이름 변경 실패: {ex.Message}";
        }

        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>선택 프로젝트 삭제. deleteConversations=false면 귀속 대화는 미분류로 해제.</summary>
    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task DeleteAsync(bool deleteConversations)
    {
        if (SelectedProject is not { IsUnassigned: false } p) return;

        try
        {
            await _projects.DeleteAsync(p.Id, deleteConversations).ConfigureAwait(false);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"삭제 실패: {ex.Message}";
        }

        await UiInvokeAsync(() => SelectedProject = null).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>선택 프로젝트를 서버로 동기화. 미지원 서버면 안내 메시지.</summary>
    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task SyncAsync()
    {
        if (SelectedProject is not { IsUnassigned: false } p) return;

        await UiInvokeAsync(() => StatusMessage = "동기화 중...").ConfigureAwait(false);

        SyncResult result;
        try
        {
            result = await _projects.SyncAsync(p.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new SyncResult(false, 0, 0, ex.Message);
        }

        await UiInvokeAsync(() =>
            StatusMessage = result.Success
                ? $"동기화 완료 (push {result.Pushed})"
                : $"동기화 미지원/실패: {result.Error}").ConfigureAwait(false);

        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 대화를 프로젝트에 편입(또는 projectId=null로 해제). 미분류 그룹 Id를 넘기면 null로 해제한다.
    /// </summary>
    [RelayCommand]
    private async Task AssignConversationAsync((string sessionId, string? projectId) arg)
    {
        var targetProjectId = arg.projectId == UnassignedId ? null : arg.projectId;

        try
        {
            await _projects.AssignConversationAsync(arg.sessionId, targetProjectId).ConfigureAwait(false);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"편입 실패: {ex.Message}";
        }

        await LoadAsync().ConfigureAwait(false);
    }

    private static Task UiInvokeAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}

/// <summary>
/// 사이드바 프로젝트 행. 자식 Conversations는 group-by 결과(읽기 전용 스냅샷).
/// IsUnassigned=true는 "미분류" 가상 그룹(rename/delete/sync 불가).
/// </summary>
public sealed partial class ProjectItemViewModel : ObservableObject
{
    /// <summary>프로젝트 Id(미분류는 <see cref="ProjectsViewModel.UnassignedId"/>).</summary>
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private int _conversationCount;
    [ObservableProperty] private bool _synced;

    public DateTimeOffset UpdatedUtc { get; }

    /// <summary>실제 프로젝트가 아닌 합성 "미분류" 그룹 여부.</summary>
    public bool IsUnassigned { get; }

    /// <summary>이 프로젝트에 귀속된 대화 세션 요약(읽기 전용).</summary>
    public ObservableCollection<ChatSessionSummary> Conversations { get; }

    public ProjectItemViewModel(
        string id,
        string name,
        int conversationCount,
        bool synced,
        DateTimeOffset updatedUtc,
        bool isUnassigned,
        IReadOnlyList<ChatSessionSummary> conversations)
    {
        Id = id;
        _name = name;
        _conversationCount = conversationCount;
        _synced = synced;
        UpdatedUtc = updatedUtc;
        IsUnassigned = isUnassigned;
        Conversations = new ObservableCollection<ChatSessionSummary>(conversations);
    }
}
