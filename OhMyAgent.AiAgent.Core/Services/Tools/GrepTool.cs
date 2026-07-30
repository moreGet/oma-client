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

            var outcome = await ScanFileAsync(file, rel, regex, matches, ct).ConfigureAwait(false);
            if (outcome == ScanOutcome.RegexTimeout)
                return ToolResult.Fail("정규식 매칭 시간 초과 — 패턴이 너무 복잡합니다(백트래킹). 더 단순한 패턴으로 시도하세요.");

            if (outcome == ScanOutcome.MatchLimitReached)
            {
                truncated = true;
                break;
            }
        }

        // 링크로 건너뛴 항목이 있으면 조용히 빠뜨리지 않고 알린다 — "검색했는데 안 나왔다"는 오해 방지.
        return skippedLinks.Count > 0
            ? ToolResult.Json(new { matches, truncated, skipped_links = skippedLinks.Count })
            : ToolResult.Json(new { matches, truncated });
    }

    /// <summary>파일 한 개를 훑은 결과. 호출부는 이 값만 보고 다음 파일로 갈지·멈출지 정한다.</summary>
    private enum ScanOutcome
    {
        /// <summary>끝까지 훑었거나 읽을 수 없어 건너뜀 — 다음 파일로.</summary>
        Done,

        /// <summary>전체 매치 상한에 도달 — 검색 종료.</summary>
        MatchLimitReached,

        /// <summary>정규식 백트래킹 타임아웃 — 검색 전체를 실패로 끝낸다.</summary>
        RegexTimeout,
    }

    /// <summary>
    /// 파일 하나를 한 줄씩 훑어 매치를 <paramref name="matches"/> 에 담는다.
    ///
    /// 파일 전체를 string[] 로 올리지 않고 흘려보낸다 — 워크스페이스에 초대형 로그/번들이 있어도
    /// 피크 메모리가 파일 크기에 비례하지 않는다.
    /// 열기·읽기 실패(바이너리/접근 불가)는 그 파일만 건너뛴다. 이미 담은 매치는 유지한다.
    /// </summary>
    private static async Task<ScanOutcome> ScanFileAsync(
        string file, string rel, Regex regex, List<object> matches, CancellationToken ct)
    {
        StreamReader reader;
        try
        {
            reader = new StreamReader(file, Encoding.UTF8);
        }
        catch
        {
            return ScanOutcome.Done;
        }

        using (reader)
        {
            var lineNumber = 0;
            try
            {
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    lineNumber++;
                    if (!regex.IsMatch(line)) continue;

                    matches.Add(new { file = rel, line = lineNumber, text = Truncate(line) });
                    if (matches.Count >= MaxMatches)
                        return ScanOutcome.MatchLimitReached;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return ScanOutcome.RegexTimeout;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return ScanOutcome.Done;   // 읽는 중 실패 — 해당 파일만 스킵.
            }
        }

        return ScanOutcome.Done;
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
