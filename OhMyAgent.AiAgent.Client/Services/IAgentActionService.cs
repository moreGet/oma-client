namespace OhMyAgent.AiAgent.Client.Services;

public interface IAgentActionService
{
    Task<string> ExecuteCreateFileAsync(string content, CancellationToken ct = default);
}
