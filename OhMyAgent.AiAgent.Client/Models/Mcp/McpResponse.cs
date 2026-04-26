using Newtonsoft.Json;

namespace OhMyAgent.AiAgent.Client.Models.Mcp;

public class McpResponse
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonProperty("id")]
    public object? Id { get; set; }

    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
    public object? Result { get; set; }

    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public McpError? Error { get; set; }

    public static McpResponse Ok(object? id, object? result)
        => new() { Id = id, Result = result };

    public static McpResponse Fail(object? id, int code, string message, object? data = null)
        => new() { Id = id, Error = new McpError { Code = code, Message = message, Data = data } };
}
