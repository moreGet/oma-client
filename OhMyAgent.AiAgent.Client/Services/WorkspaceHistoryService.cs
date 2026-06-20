using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 최근 작업 공간 히스토리 — 실제 구현. AppSettings.RecentWorkspaces에 영속한다 (B).
/// </summary>
/// <remarks>
/// 재진입 회피: settings를 직접 변경하고 <c>SaveAsync</c>만 호출하며,
/// settings의 <c>RaiseSettingsChanged</c>는 호출하지 않는다(App의 SettingsChanged → AddAsync 루프 방지).
/// 변경 알림은 전용 <see cref="HistoryChanged"/> 이벤트로만 전파한다.
/// </remarks>
public sealed class WorkspaceHistoryService : IWorkspaceHistoryService
{
    private const int Cap = 10;

    private readonly ISettingsService _settings;
    private readonly object _gate = new();

    public WorkspaceHistoryService(ISettingsService settings)
    {
        _settings = settings;
    }

    public event EventHandler? HistoryChanged;

    public IReadOnlyList<WorkspaceHistoryEntry> GetRecent()
    {
        lock (_gate)
        {
            var entries = _settings.Current.RecentWorkspaces ?? [];
            return entries
                .OrderByDescending(e => e.LastUsedUtc)
                .Take(Cap)
                .ToList();
        }
    }

    public async Task AddAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalized = Normalize(path);
        var displayName = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(displayName))
            displayName = normalized;

        lock (_gate)
        {
            var list = _settings.Current.RecentWorkspaces ?? [];
            list.RemoveAll(e => string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new WorkspaceHistoryEntry
            {
                Path = normalized,
                DisplayName = displayName,
                LastUsedUtc = DateTimeOffset.UtcNow
            });
            if (list.Count > Cap)
                list.RemoveRange(Cap, list.Count - Cap);
            _settings.Current.RecentWorkspaces = list;
        }

        // settings 저장만 수행 — RaiseSettingsChanged는 호출하지 않는다(재진입 루프 회피).
        await _settings.SaveAsync().ConfigureAwait(false);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalized = Normalize(path);
        bool changed;
        lock (_gate)
        {
            var list = _settings.Current.RecentWorkspaces ?? [];
            var removed = list.RemoveAll(e =>
                string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));
            changed = removed > 0;
            _settings.Current.RecentWorkspaces = list;
        }

        if (!changed)
            return;

        await _settings.SaveAsync().ConfigureAwait(false);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task TouchAsync(string path, CancellationToken ct = default)
        // Add는 기존 항목을 제거 후 최신 시각으로 재삽입하므로 Touch와 의미가 동일하다.
        => AddAsync(path, ct);

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            // 정규화 불가(잘못된 경로 문자 등)면 원본을 그대로 사용한다.
            return path;
        }
    }
}
