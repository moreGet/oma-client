using Newtonsoft.Json;

namespace OhMyAgent.AiAgent.Client.Models;

public record UserMessagesDto(
    [property: JsonProperty("session_id")]  string SessionId,
    [property: JsonProperty("content")]     string Content,
    [property: JsonProperty("domain")]      string Domain
);
