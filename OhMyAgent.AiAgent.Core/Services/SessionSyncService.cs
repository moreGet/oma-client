using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// <see cref="ISessionSyncService"/> 구현. IAgentApiClient(원격)와 IChatHistoryService(로컬)를
/// 연결해 대화 세션을 양방향 동기화한다. 충돌은 updated_at 최신 우선 — pull은 원격이 더 최신일 때만
/// 로컬을 덮어쓰고, push는 항상 업로드한다. 모든 경로 graceful(예외로 UI를 깨지 않는다).
/// </summary>
public sealed class SessionSyncService(IAgentApiClient api, IChatHistoryService chatHistory) : ISessionSyncService
{
    private readonly IAgentApiClient _api = api;
    private readonly IChatHistoryService _chatHistory = chatHistory;

    public async Task PushAsync(ChatSessionRecord record, CancellationToken ct = default)
    {
        try
        {
            // 로컬 record를 그대로 직렬화해 data(불투명)로 업로드.
            var json = JsonSerializer.Serialize(record, AgentJson.Options);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.Clone();

            await _api.PutRemoteSessionAsync(record.Id, record.Title, data, ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort — 업로드 실패가 로컬 동작/턴을 막지 않는다.
        }
    }

    public async Task<int> PullMergeAsync(CancellationToken ct = default)
    {
        var summaries = await _api.ListRemoteSessionsAsync(ct).ConfigureAwait(false);
        if (summaries is null)
            return 0;   // 미구현/오프라인 → 병합 없음

        var merged = 0;

        foreach (var summary in summaries)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(summary.Id))
                    continue;

                // 로컬이 있고 원격이 더 최신이 아니면 skip(로컬 우선 유지).
                ChatSessionRecord? local = null;
                try { local = await _chatHistory.LoadAsync(summary.Id, ct).ConfigureAwait(false); }
                catch { local = null; }

                if (local is not null && summary.UpdatedAt <= local.UpdatedUtc)
                    continue;

                // 원격 전체를 받아 data → ChatSessionRecord 역직렬화.
                var remote = await _api.GetRemoteSessionAsync(summary.Id, ct).ConfigureAwait(false);
                if (remote is null)
                    continue;

                ChatSessionRecord? record;
                try
                {
                    record = remote.Data.Deserialize<ChatSessionRecord>(AgentJson.Options);
                }
                catch
                {
                    continue;   // 손상 data → skip
                }

                if (record is null)
                    continue;

                // Id 불일치(서버/클라 어긋남) → skip.
                if (!string.Equals(record.Id, summary.Id, StringComparison.Ordinal))
                    continue;

                await _chatHistory.SaveAsync(record, ct).ConfigureAwait(false);
                merged++;
            }
            catch
            {
                // 항목별 graceful — 한 세션 실패가 나머지 병합을 막지 않는다.
            }
        }

        return merged;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await _api.DeleteRemoteSessionAsync(id, ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort — 원격 삭제 실패가 로컬 삭제를 무효화하지 않는다.
        }
    }
}
