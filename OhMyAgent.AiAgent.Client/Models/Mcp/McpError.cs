using Newtonsoft.Json;

namespace OhMyAgent.AiAgent.Client.Models.Mcp;

public class McpError
{
    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public object? Data { get; set; }

    // JSON-RPC 2.0 표준 에러 코드
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // 커스텀 에러 코드 (Anthropic MCP 권장 범위 -32000 ~ -32099)
    public const int SecurityViolation = -32000;
    public const int ExecutionFailed = -32001;
    public const int ExecutionTimeout = -32002;
}
