using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class ReadFileTool : ITool
{
    private const int MaxChars = 100_000; // 문자 기준 상한(멀티바이트 안전). ASCII ~100KB, 한글 ~200KB 상당
    private const string TruncNotice = "\n\n[... 출력이 상한을 초과해 잘렸습니다. start_line/end_line 으로 범위를 좁히세요 ...]";

    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"path":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"}},"required":["path"]}
        """);

    public string Name => "read_file";
    public string Description => "Read a UTF-8 text file in the workspace. Optionally slice by 1-based inclusive line range.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var path = ToolSchemas.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Fail("path 가 비어 있습니다.");

        var full = ctx.Workspace.ResolvePath(path);
        if (!File.Exists(full))
        {
            // 단순히 "없습니다"로 끝내면 모델에게 다음 수가 없다 — 닮은 이름 후보와 대안을 함께 준다.
            return ToolResult.Fail(NotFoundHelp.ForFile(ctx.Workspace, path, ct));
        }

        var startLine = ToolSchemas.GetInt(args, "start_line");
        var endLine = ToolSchemas.GetInt(args, "end_line");

        string content;
        if (startLine.HasValue || endLine.HasValue)
        {
            // 지연 열거 — 대형 파일에서 필요한 라인 범위까지만 읽어 메모리 절약.
            var from = Math.Max(1, startLine ?? 1);
            var count = endLine.HasValue ? Math.Max(0, endLine.Value - from + 1) : int.MaxValue;
            var sb = new StringBuilder();
            var taken = 0;
            foreach (var line in File.ReadLines(full, Encoding.UTF8).Skip(from - 1))
            {
                ct.ThrowIfCancellationRequested();
                if (taken >= count || sb.Length > MaxChars) break;
                if (taken > 0) sb.Append('\n');
                sb.Append(line);
                taken++;
            }
            content = sb.ToString();
        }
        else
        {
            // 전체 파일을 한 번에 로드하지 않고 상한(+1)까지만 읽어 초대형 파일에서도 메모리 안전.
            using var reader = new StreamReader(full, Encoding.UTF8);
            var buffer = new char[MaxChars + 1];
            var read = 0;
            int r;
            while (read < buffer.Length && (r = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false)) > 0)
                read += r;
            if (read > MaxChars)
                return ToolResult.Ok(new string(buffer, 0, MaxChars) + TruncNotice);
            content = new string(buffer, 0, read);
        }

        // 문자 기준 상한(멀티바이트 안전) — 범위 읽기 결과가 상한을 넘으면 절단.
        if (content.Length > MaxChars)
            return ToolResult.Ok(content[..MaxChars] + TruncNotice);

        return ToolResult.Ok(content);
    }
}
