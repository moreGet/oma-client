using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>최근 작업 공간(워크스페이스) 히스토리. AppSettings.RecentWorkspaces를 래핑한다 (B).</summary>
public interface IWorkspaceHistoryService
{
    /// <summary>최근순 정렬된 스냅샷(상한 10).</summary>
    IReadOnlyList<WorkspaceHistoryEntry> GetRecent();

    /// <summary>경로 추가/갱신: 정규화→대소문자 무시 중복 제거→LastUsedUtc=now→최근순 정렬→상한 10→settings 저장. 변경 알림 발생.</summary>
    Task AddAsync(string path, CancellationToken ct = default);

    /// <summary>해당 경로 제거 후 저장.</summary>
    Task RemoveAsync(string path, CancellationToken ct = default);

    /// <summary>이미 존재하는 경로의 LastUsedUtc만 갱신(없으면 Add와 동일).</summary>
    Task TouchAsync(string path, CancellationToken ct = default);

    /// <summary>목록 변경 시 발생(VM이 구독해 컬렉션 갱신).</summary>
    event EventHandler? HistoryChanged;
}
