using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IChatService
{
    IAsyncEnumerable<string> StreamResponseAsync(UserMessagesDto request, CancellationToken ct = default);
    Task<bool> CheckConnectionAsync(CancellationToken ct = default);
}
