using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 서버에 전송되는 도구 스키마 (AgentRequest.Tools 항목).
/// Parameters 는 JSON Schema object.
/// </summary>
public sealed record ToolSchema(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")]  JsonElement Parameters);
