using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>서버가 제공할 동작 힌트 1건(현재 stub로 빈 목록).</summary>
public sealed record Suggestion(
    [property: JsonPropertyName("text")]   string Text,           // 표시 문구 ("~~ 해보세요")
    [property: JsonPropertyName("prompt")] string? Prompt = null, // 클릭 시 InputText에 채울 실제 프롬프트
    [property: JsonPropertyName("icon")]   string? Icon = null);  // Segoe Fluent 글리프(선택)
