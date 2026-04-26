using Newtonsoft.Json;

namespace OhMyAgent.AiAgent.Client.Models;

public record AgentResponsesDto(
    [property: JsonProperty("session_id")]   string SessionId,
    [property: JsonProperty("content")]      string Content,
    [property: JsonProperty("is_complete")]  bool IsComplete,
    [property: JsonProperty("action_token")] string? ActionToken
);
