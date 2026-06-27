using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 프로젝트 로컬 영속 — 파일당 1프로젝트 JSON. ChatHistoryService 와 동일 패턴(원자적 upsert, AgentJson.Options).
/// 대화 귀속/카운트는 IChatHistoryService 를 통해 세션을 읽어 산출, 동기화는 IAgentApiClient 위임.
/// </summary>
public sealed class ProjectService(IChatHistoryService history, IAgentApiClient api) : IProjectService
{
    private static readonly string ProjectsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OhMyAgent", "projects");

    private readonly object _ioLock = new();

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken ct = default)
    {
        // 세션 → ProjectId 카운트(미분류 null 제외).
        var sessions = await history.ListAsync(ct).ConfigureAwait(false);
        var counts = sessions
            .Where(s => !string.IsNullOrEmpty(s.ProjectId))
            .GroupBy(s => s.ProjectId!)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return await Task.Run(() =>
        {
            lock (_ioLock)
            {
                var summaries = new List<ProjectSummary>();
                if (!Directory.Exists(ProjectsDirectory))
                    return (IReadOnlyList<ProjectSummary>)summaries;

                foreach (var file in Directory.EnumerateFiles(ProjectsDirectory, "*.json"))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var json = File.ReadAllText(file);
                        var record = JsonSerializer.Deserialize<ProjectRecord>(json, AgentJson.Options);
                        if (record is null)
                            continue;

                        var count = counts.TryGetValue(record.Id, out var c) ? c : 0;
                        summaries.Add(new ProjectSummary(
                            record.Id, record.Name, count, record.UpdatedUtc, record.Synced));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ProjectService] skip corrupt '{file}': {ex.Message}");
                    }
                }

                summaries.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
                return (IReadOnlyList<ProjectSummary>)summaries;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<ProjectRecord?> LoadAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return await Task.Run(() =>
        {
            lock (_ioLock)
            {
                var path = PathFor(id);
                if (!File.Exists(path))
                    return null;
                try
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<ProjectRecord>(json, AgentJson.Options);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProjectService] LoadAsync failed for '{id}': {ex.Message}");
                    return null;
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<ProjectRecord> CreateAsync(string name, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ProjectRecord
        {
            Id = Guid.NewGuid().ToString(),
            Name = string.IsNullOrWhiteSpace(name) ? "새 프로젝트" : name.Trim(),
            CreatedUtc = now,
            UpdatedUtc = now,
            Synced = false,
            RemoteId = null
        };
        await SaveAsync(record, ct).ConfigureAwait(false);
        return record;
    }

    public async Task RenameAsync(string id, string name, CancellationToken ct = default)
    {
        var existing = await LoadAsync(id, ct).ConfigureAwait(false);
        if (existing is null)
            return;

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name.Trim(),
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await SaveAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, bool deleteConversations, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        // 원격 동기화된 프로젝트면 서버측도 삭제(graceful — 미지원/오프라인이면 no-op).
        var project = await LoadAsync(id, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(project?.RemoteId))
            await api.DeleteRemoteProjectAsync(project!.RemoteId!, ct).ConfigureAwait(false);

        // 귀속 세션 처리: 삭제 또는 미분류로 해제.
        var sessions = await history.ListAsync(ct).ConfigureAwait(false);
        foreach (var s in sessions.Where(s => string.Equals(s.ProjectId, id, StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            if (deleteConversations)
                await history.DeleteAsync(s.Id, ct).ConfigureAwait(false);
            else
                await AssignConversationAsync(s.Id, null, ct).ConfigureAwait(false);
        }

        await Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    var path = PathFor(id);
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProjectService] DeleteAsync failed for '{id}': {ex.Message}");
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task AssignConversationAsync(string sessionId, string? projectId, CancellationToken ct = default)
    {
        var record = await history.LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (record is null)
            return;

        var updated = record with
        {
            ProjectId = projectId,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await history.SaveAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task<SyncResult> SyncAsync(string projectId, CancellationToken ct = default)
    {
        var project = await LoadAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return new SyncResult(false, 0, 0, "프로젝트를 찾을 수 없습니다.");

        try
        {
            // 1) 프로젝트 upsert. 서버는 client_id(클라 GUID)↔id 로 멱등 매핑하므로
            //    안정 키인 로컬 ProjectRecord.Id 를 client_id 로 전송한다.
            var remote = await api.UpsertRemoteProjectAsync(
                new RemoteProjectUpsert(project.Id, project.Name), ct).ConfigureAwait(false);

            // 2) 귀속 대화 push.
            var sessions = await history.ListAsync(ct).ConfigureAwait(false);
            var owned = sessions
                .Where(s => string.Equals(s.ProjectId, projectId, StringComparison.Ordinal))
                .ToList();

            var pushed = 0;
            foreach (var summary in owned)
            {
                ct.ThrowIfCancellationRequested();
                var full = await history.LoadAsync(summary.Id, ct).ConfigureAwait(false);
                if (full is null)
                    continue;

                await api.UpsertRemoteConversationAsync(
                    remote.Id,
                    new RemoteConversation(full.Id, full.Title, full.CreatedUtc, full.UpdatedUtc, full.Messages),
                    ct).ConfigureAwait(false);
                pushed++;
            }

            // 3) 동기화 메타 저장.
            var synced = project with
            {
                Synced = true,
                RemoteId = remote.Id,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            await SaveAsync(synced, ct).ConfigureAwait(false);

            return new SyncResult(true, pushed, 0, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentException ex)
        {
            return new SyncResult(false, 0, 0, ex.Message);
        }
        catch (Exception ex)
        {
            return new SyncResult(false, 0, 0, ex.Message);
        }
    }

    private async Task SaveAsync(ProjectRecord record, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(ProjectsDirectory);
                    var path = PathFor(record.Id);
                    var tmp  = path + ".tmp";
                    var json = JsonSerializer.Serialize(record, AgentJson.Options);
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);   // 원자적 교체
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProjectService] SaveAsync failed for '{record.Id}': {ex.Message}");
                    throw new AgentException($"프로젝트 저장 실패: {record.Id}", ex);
                }
            }
        }, ct).ConfigureAwait(false);
    }

    private static string PathFor(string id)
    {
        var safe = Path.GetFileName(id);   // 경로 탈출 방지: 파일명만 사용.
        return Path.Combine(ProjectsDirectory, safe + ".json");
    }
}
