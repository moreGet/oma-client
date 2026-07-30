using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class EditFileTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"old_string":{"type":"string"},"new_string":{"type":"string"},"replace_all":{"type":"boolean"}},"required":["path","old_string","new_string"]}
        """);

    public string Name => "edit_file";
    public string Description => "Replace an exact substring in a workspace file. old_string must be unique unless replace_all is true.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var oldString = ToolSchemas.GetString(args, "old_string");
        var newString = ToolSchemas.GetString(args, "new_string") ?? "";
        var replaceAll = ToolSchemas.GetBool(args, "replace_all");

        if (!ToolPaths.TryGetRequiredPath(args, out var path, out var error))
            return error;
        if (string.IsNullOrEmpty(oldString))
            return ToolResult.Fail("old_string 이 비어 있습니다.");

        var full = ctx.Workspace.ResolvePath(path);
        if (!File.Exists(full))
            return ToolResult.Fail($"파일이 존재하지 않습니다: {path}");

        var text = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);

        var count = CountOccurrences(text, oldString);
        if (count == 0)
            return ToolResult.Fail("old_string 을 찾을 수 없습니다.");
        if (count > 1 && !replaceAll)
            return ToolResult.Fail($"old_string 이 {count}회 발견되어 모호합니다. replace_all=true 를 쓰거나 더 구체적인 문자열을 사용하세요.");

        string updated;
        int replacements;
        if (replaceAll)
        {
            updated = text.Replace(oldString, newString);
            replacements = count;
        }
        else
        {
            var idx = text.IndexOf(oldString, StringComparison.Ordinal);
            updated = text[..idx] + newString + text[(idx + oldString.Length)..];
            replacements = 1;
        }

        await ToolPaths.WriteUtf8NoBomAsync(full, updated, ct).ConfigureAwait(false);

        return ToolResult.Json(new { path, replacements });
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
