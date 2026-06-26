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
/// 대화 세션 로컬 영속 — 실제 구현. 파일당 1세션 JSON, 직렬화는 AgentJson.Options 재사용 (C).
/// </summary>
public sealed class ChatHistoryService : IChatHistoryService
{
    private const string DefaultTitle = "새 대화";
    private const int TitleMaxLength = 40;

    private static readonly string SessionsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OhMyAgent", "sessions");

    private readonly object _ioLock = new();

    public async Task<IReadOnlyList<ChatSessionSummary>> ListAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            lock (_ioLock)
            {
                var summaries = new List<ChatSessionSummary>();
                if (!Directory.Exists(SessionsDirectory))
                    return (IReadOnlyList<ChatSessionSummary>)summaries;

                foreach (var file in Directory.EnumerateFiles(SessionsDirectory, "*.json"))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var json = File.ReadAllText(file);
                        var record = JsonSerializer.Deserialize<ChatSessionRecord>(json, AgentJson.Options);
                        if (record is null)
                            continue;

                        summaries.Add(new ChatSessionSummary(
                            record.Id,
                            record.Title,
                            record.UpdatedUtc,
                            record.WorkspaceRoot,
                            record.Messages.Count,
                            record.ProjectId));
                    }
                    catch (Exception ex)
                    {
                        // 손상 파일은 건너뛴다.
                        Debug.WriteLine($"[ChatHistoryService] skip corrupt '{file}': {ex.Message}");
                    }
                }

                summaries.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
                return (IReadOnlyList<ChatSessionSummary>)summaries;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<ChatSessionRecord?> LoadAsync(string id, CancellationToken ct = default)
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
                    return JsonSerializer.Deserialize<ChatSessionRecord>(json, AgentJson.Options);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ChatHistoryService] LoadAsync failed for '{id}': {ex.Message}");
                    return null;
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(ChatSessionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(SessionsDirectory);
                    var path = PathFor(record.Id);
                    var tmp  = path + ".tmp";
                    var json = JsonSerializer.Serialize(record, AgentJson.Options);
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);   // 부분 쓰기 방지: 원자적 교체
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ChatHistoryService] SaveAsync failed for '{record.Id}': {ex.Message}");
                    throw new AgentException($"세션 저장 실패: {record.Id}", ex);
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

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
                    Debug.WriteLine($"[ChatHistoryService] DeleteAsync failed for '{id}': {ex.Message}");
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public ChatSessionRecord CreateNew(string? workspaceRoot = null, string? projectId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ChatSessionRecord
        {
            Id = Guid.NewGuid().ToString(),
            Title = DefaultTitle,
            CreatedUtc = now,
            UpdatedUtc = now,
            WorkspaceRoot = workspaceRoot,
            ProjectId = projectId,
            Messages = []
        };
    }

    public string BuildTitle(IReadOnlyList<AgentMessage> messages)
    {
        var firstUser = messages?
            .FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content));
        var content = firstUser?.Content;
        if (string.IsNullOrWhiteSpace(content))
            return DefaultTitle;

        var oneLine = content
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        // 연속 공백 축약.
        oneLine = string.Join(' ', oneLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (oneLine.Length == 0)
            return DefaultTitle;

        return oneLine.Length > TitleMaxLength
            ? oneLine[..TitleMaxLength] + "…"
            : oneLine;
    }

    private static string PathFor(string id)
    {
        // id는 호출자(Guid 또는 record.Id)에서 오나, 경로 탈출 방지를 위해 파일명만 사용.
        var safe = Path.GetFileName(id);
        return Path.Combine(SessionsDirectory, safe + ".json");
    }
}
