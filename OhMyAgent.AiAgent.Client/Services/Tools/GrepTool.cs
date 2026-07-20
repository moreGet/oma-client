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
            // ReDoS 방어 — catastrophic backtracking 이 CPU 를 무한 점유하지 않도록 매칭 타임아웃.
            regex = new Regex(pattern, opts, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail($"잘못된 정규식: {ex.Message}");
        }

        var fileRegex = string.IsNullOrWhiteSpace(globPattern) ? null : GlobMatcher.Compile(globPattern);
        var matches = new List<object>();
        var truncated = false;

        // 정션/심볼릭 링크를 따라가지 않는 열거 — 링크를 따라가면 워크스페이스 밖 파일 내용이
        // 검색 결과로 새어 나온다(에이전트가 mklink /j 로 링크를 스스로 만들 수 있다).
        var skippedLinks = new List<string>();

        foreach (var file in SafeFileWalk.EnumerateFiles(baseDir, skippedLinks, ct, skipIgnoredDirs: true))
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

            try
            {
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
            }
            catch (RegexMatchTimeoutException)
            {
                return ToolResult.Fail("정규식 매칭 시간 초과 — 패턴이 너무 복잡합니다(백트래킹). 더 단순한 패턴으로 시도하세요.");
            }

            if (truncated) break;
        }

        // 링크로 건너뛴 항목이 있으면 조용히 빠뜨리지 않고 알린다 — "검색했는데 안 나왔다"는 오해 방지.
        return skippedLinks.Count > 0
            ? ToolResult.Json(new { matches, truncated, skipped_links = skippedLinks.Count })
            : ToolResult.Json(new { matches, truncated });
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
