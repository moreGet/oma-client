namespace OhMyAgent.AiAgent.Client.Services;

public sealed class AgentException(string message, Exception? inner = null)
    : Exception(message, inner);
