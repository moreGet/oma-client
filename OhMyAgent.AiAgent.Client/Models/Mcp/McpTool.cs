using Newtonsoft.Json;

namespace OhMyAgent.AiAgent.Client.Models.Mcp;

public class McpTool
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("inputSchema")]
    public object InputSchema { get; set; } = new { };
}
