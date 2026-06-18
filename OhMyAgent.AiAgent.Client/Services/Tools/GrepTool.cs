using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class GrepTool : ITool
{
    private const int MaxMatches = 500;

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"glob":{"type":"string"},"ignore_case":{"type":"boolean"}},"required":["pattern"]}
        """);

    public string Name => "grep";
    public string Description => "Search file contents with a regular expression across matching files in the workspace.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var pattern = ToolSchemas.GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResult.Fail("pattern 이 비어 있습니다.");

        var path = ToolSchemas.GetString(args, "path");
        var globPattern = ToolSchemas.GetString(args, "glob");
        var ignoreCase = ToolSchemas.GetBool(args, "ignore_case");

        var baseDir = ctx.Workspace.ResolvePath(path ?? "");
        if (!Directory.Exists(baseDir))
            return ToolResult.Fail($"디렉토리가 존재하지 않습니다: {(string.IsNullOrEmpty(path) ? "." : path)}");

        Regex regex;
        try
        {
            var opts = RegexOptions.Compiled;
            if (ignoreCase) opts |= RegexOptions.IgnoreCase;
            regex = new Regex(pattern, opts);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail($"잘못된 정규식: {ex.Message}");
        }

        var fileRegex = string.IsNullOrWhiteSpace(globPattern) ? null : GlobMatcher.Compile(globPattern);
        var matches = new List<object>();
        var truncated = false;

        foreach (var file in EnumerateFiles(baseDir))
        {
            ct.ThrowIfCancellationRequested();

            var rel = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            if (fileRegex is not null && !fileRegex.IsMatch(rel))
                continue;

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, Encoding.UTF8, ct).ConfigureAwait(false);
            }
            catch
            {
                continue; // 바이너리/접근 불가 파일 스킵.
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;

                matches.Add(new { file = rel, line = i + 1, text = Truncate(lines[i]) });
                if (matches.Count >= MaxMatches)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated) break;
        }

        return ToolResult.Json(new { matches, truncated });
    }

    private static IEnumerable<string> EnumerateFiles(string baseDir)
    {
        var pending = new Stack<string>();
        pending.Push(baseDir);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { continue; }

            foreach (var f in files) yield return f;
            foreach (var d in subDirs) pending.Push(d);
        }
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
