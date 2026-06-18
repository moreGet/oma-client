using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 에이전트 와이어 DTO 전용 공유 System.Text.Json 옵션.
/// snake/lower-case enum (system/user/assistant/tool, tool_use 등) + null 생략.
/// </summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
