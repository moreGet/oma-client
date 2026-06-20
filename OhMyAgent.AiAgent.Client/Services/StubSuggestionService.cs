using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 동작 힌트 stub — 항상 빈 목록 (G).
/// 미래에 GET /api/v1/agent/suggestions (API_CONTRACT §8.1) 연동으로 교체한다.
/// </summary>
public sealed class StubSuggestionService : ISuggestionService
{
    private static readonly IReadOnlyList<Suggestion> Empty = [];

    public Task<IReadOnlyList<Suggestion>> GetSuggestionsAsync(string workspaceRoot, CancellationToken ct = default)
        => Task.FromResult(Empty);
}
