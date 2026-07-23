using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// A2A 요청 본문 → 프롬프트 추출(스펙 §1-F). 두 입력 형태 수용:
///   1) 표준 AgentRequest 본문 — messages 의 마지막 user 메시지 텍스트를 프롬프트로.
///   2) 경량 {"prompt":"..."} — curl/타 언어 클라이언트 편의.
/// B 는 자기 orchestrator·시스템 프롬프트·도구로 새 세션을 구성하므로 요청의 model/tools 는 참고만.
/// </summary>
public static class A2aRequest
{
    public static async Task<string?> ReadPromptAsync(Stream body, CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return ExtractPrompt(raw);
    }

    /// <summary>순수 파서 — 본문 문자열에서 프롬프트를 추출한다(테스트 대상).</summary>
    public static string? ExtractPrompt(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // 형태 2: {"prompt":"..."}
            if (root.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String)
                return Clean(p.GetString());

            // 형태 1: AgentRequest — messages 의 마지막 user turn.
            if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            {
                string? lastUser = null;
                foreach (var m in msgs.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    if (!m.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String)
                        continue;
                    if (!string.Equals(role.GetString(), "user", System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        lastUser = content.GetString();
                }
                return Clean(lastUser);
            }

            return null;
        }
    }

    private static string? Clean(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;
}
