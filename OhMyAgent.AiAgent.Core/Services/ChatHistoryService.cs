using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private readonly JsonFileStore<ChatSessionRecord> _store =
        new(SessionsDirectory, "ChatHistoryService", "세션");

    public async Task<IReadOnlyList<ChatSessionSummary>> ListAsync(CancellationToken ct = default)
    {
        // 요약 6필드만 쓰지만 레코드 전체를 역직렬화해야 한다(Messages.Count).
        // 저장소가 파일을 하나씩 읽어 투영하고 버리므로, 긴 대화 파일이 세션 수만큼 쌓이지는 않는다.
        var summaries = await _store.ListAsync(record => new ChatSessionSummary(
            record.Id,
            record.Title,
            record.UpdatedUtc,
            record.WorkspaceRoot,
            record.Messages.Count,
            record.ProjectId), ct).ConfigureAwait(false);

        summaries.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));   // 최신 갱신 우선
        return summaries;
    }

    public async Task<ChatSessionRecord?> LoadAsync(string id, CancellationToken ct = default)
        => await _store.LoadAsync(id, ct).ConfigureAwait(false);

    public async Task SaveAsync(ChatSessionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _store.SaveAsync(record.Id, record, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
        => await _store.DeleteAsync(id, ct).ConfigureAwait(false);

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

}
