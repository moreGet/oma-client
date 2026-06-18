using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 모델이 요청한 도구 호출. Arguments 는 모델이 생성한 raw JSON object이며
/// 각 ITool 이 직접 파싱한다.
/// </summary>
public sealed record ToolCall(
    [property: JsonPropertyName("id")]        string Id,
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("arguments")] JsonElement Arguments);
