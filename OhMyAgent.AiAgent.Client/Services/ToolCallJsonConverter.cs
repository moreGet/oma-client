using System.Text.Json;
using System.Text.Json.Serialization;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// ToolCall 직렬화 컨버터. 서버 와이어 계약에 맞춰 <c>arguments</c> 를
/// 쓰기 시 JSON <b>문자열</b>(예: <c>"arguments":"{\"path\":\".\"}"</c>)로 출력하고,
/// 읽기 시 문자열이면 다시 객체로 복원한다(대칭성/안전).
/// 인메모리 표현(<see cref="ToolCall.Arguments"/>)은 항상 JSON object 로 유지된다.
/// </summary>
public sealed class ToolCallJsonConverter : JsonConverter<ToolCall>
{
    public override ToolCall Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var id   = root.TryGetProperty("id", out var i)   ? i.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        JsonElement args;
        if (root.TryGetProperty("arguments", out var a))
        {
            if (a.ValueKind == JsonValueKind.String)
            {
                var raw = a.GetString();
                using var inner = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                args = inner.RootElement.Clone();
            }
            else
            {
                args = a.Clone();
            }
        }
        else
        {
            using var e = JsonDocument.Parse("{}");
            args = e.RootElement.Clone();
        }

        return new ToolCall(id, name, args);
    }

    public override void Write(Utf8JsonWriter writer, ToolCall value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        // arguments(JsonElement 객체) → JSON 텍스트를 문자열 값으로 이스케이프하여 기록.
        writer.WriteString("arguments", value.Arguments.GetRawText());
        writer.WriteEndObject();
    }
}
