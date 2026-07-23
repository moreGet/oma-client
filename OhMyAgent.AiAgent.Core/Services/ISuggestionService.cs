using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>워크스페이스 컨텍스트 기반 동작 힌트 제공 (G).</summary>
public interface ISuggestionService
{
    /// <summary>워크스페이스 컨텍스트 기반 동작 힌트. 현재 stub: 항상 빈 목록.</summary>
    Task<IReadOnlyList<Suggestion>> GetSuggestionsAsync(string workspaceRoot, CancellationToken ct = default);
}
