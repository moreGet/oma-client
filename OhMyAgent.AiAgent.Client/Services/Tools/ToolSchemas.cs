using System.Text.Json;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>JSON Schema 문자열을 JsonElement 로 한 번 파싱하는 헬퍼 + 인자 파싱 유틸.</summary>
internal static class ToolSchemas
{
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static string? GetString(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public static int? GetInt(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out var v))
            return v;
        return null;
    }

    public static bool GetBool(JsonElement args, string name, bool fallback = false)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var p))
        {
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }
}
