using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 프로젝트(대화 컨테이너) 로컬 영속 — %APPDATA%/OhMyAgent/projects/{id}.json.
/// 로컬 우선, 서버 동기화는 선택(SyncAsync). ConversationCount 는 세션 group-by 산출.
/// </summary>
public interface IProjectService
{
    /// <summary>projects 디렉토리를 스캔해 요약 목록(UpdatedUtc desc). ConversationCount 는 세션 ProjectId group-by.</summary>
    Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>id의 전체 record 로드. 없으면 null.</summary>
    Task<ProjectRecord?> LoadAsync(string id, CancellationToken ct = default);

    /// <summary>새 프로젝트 생성·저장(Id=Guid, Synced=false).</summary>
    Task<ProjectRecord> CreateAsync(string name, CancellationToken ct = default);

    /// <summary>이름 변경·재저장(UpdatedUtc 갱신).</summary>
    Task RenameAsync(string id, string name, CancellationToken ct = default);

    /// <summary>프로젝트 삭제. deleteConversations=true면 귀속 세션도 삭제, 아니면 미분류로 해제.</summary>
    Task DeleteAsync(string id, bool deleteConversations, CancellationToken ct = default);

    /// <summary>세션의 ProjectId 갱신·재저장(null=미분류).</summary>
    Task AssignConversationAsync(string sessionId, string? projectId, CancellationToken ct = default);

    /// <summary>서버로 프로젝트+귀속 대화 push. 미구현 서버면 Success=false 의 graceful 결과.</summary>
    Task<SyncResult> SyncAsync(string projectId, CancellationToken ct = default);
}

/// <summary>동기화 결과. Success=false 시 Error에 사유.</summary>
public sealed record SyncResult(bool Success, int Pushed, int Pulled, string? Error);
