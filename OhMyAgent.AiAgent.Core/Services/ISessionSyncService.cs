using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 대화 세션을 서버(/api/v1/agent/sessions)와 동기화해 여러 PC에서 히스토리를 공유한다.
/// 모든 동작은 best-effort(graceful) — 서버 미구현/오프라인/403이어도 로컬 동작을 막지 않는다.
/// </summary>
public interface ISessionSyncService
{
    /// <summary>로컬 record를 서버로 push(upsert). 실패는 무시(graceful).</summary>
    Task PushAsync(ChatSessionRecord record, CancellationToken ct = default);

    /// <summary>
    /// 서버 세션 목록을 받아 로컬과 병합한다. 로컬이 없거나 원격이 더 최신(updated_at)일 때만
    /// 로컬을 덮어쓴다. 병합(저장)한 건수를 반환. 항목별 try/catch로 전부 graceful.
    /// </summary>
    Task<int> PullMergeAsync(CancellationToken ct = default);

    /// <summary>서버에서 세션 삭제. 실패는 무시(graceful no-op).</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
