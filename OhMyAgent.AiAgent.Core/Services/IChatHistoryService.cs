using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>대화 세션 로컬 영속 — %APPDATA%/OhMyAgent/sessions/{id}.json (C).</summary>
public interface IChatHistoryService
{
    /// <summary>sessions 디렉토리를 스캔해 요약 목록을 UpdatedUtc desc로 반환. 손상 파일은 건너뜀.</summary>
    Task<IReadOnlyList<ChatSessionSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>id의 전체 record 로드. 없으면 null.</summary>
    Task<ChatSessionRecord?> LoadAsync(string id, CancellationToken ct = default);

    /// <summary>record를 {id}.json에 원자적 upsert(임시파일→교체). UpdatedUtc는 호출자가 세팅.</summary>
    Task SaveAsync(ChatSessionRecord record, CancellationToken ct = default);

    /// <summary>{id}.json 삭제(없어도 무해).</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>새 빈 record 생성(Id=Guid, Title="새 대화", Created=Updated=now, Messages=[]). 디스크 미기록. projectId로 프로젝트 귀속 가능.</summary>
    ChatSessionRecord CreateNew(string? workspaceRoot = null, string? projectId = null);

    /// <summary>메시지 목록으로부터 Title 생성: 첫 user 메시지 Content를 1줄·앞 40자로 요약(없으면 "새 대화").</summary>
    string BuildTitle(IReadOnlyList<AgentMessage> messages);
}
