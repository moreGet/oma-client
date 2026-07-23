using System.Collections.Generic;
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

    /// <summary>JSON 배열을 문자열 리스트로 변환(문자열/숫자/불리언/널 안전 변환). 쓰기 도구 공용.</summary>
    public static List<string> ToStringList(JsonElement arr)
    {
        var list = new List<string>();
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                list.Add(e.ValueKind switch
                {
                    JsonValueKind.String => e.GetString() ?? "",
                    JsonValueKind.Null   => "",
                    JsonValueKind.Number => e.GetRawText(),
                    JsonValueKind.True   => "true",
                    JsonValueKind.False  => "false",
                    _                    => e.GetRawText(),
                });
        return list;
    }
}
