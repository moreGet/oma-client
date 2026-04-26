using System.Collections.Generic;
using Newtonsoft.Json;

namespace OhMyAgent.AiAgent.Client.Models.Mcp;

public class McpRequest
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonProperty("id")]
    public object? Id { get; set; }

    [JsonProperty("method")]
    public string Method { get; set; } = string.Empty;

    [JsonProperty("params")]
    public Dictionary<string, object?>? Params { get; set; }
}
