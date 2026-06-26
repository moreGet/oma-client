using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class ClipboardWriteTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}
        """);

    public string Name => "clipboard_write";
    public string Description => "Write the given text to the Windows clipboard.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.Write;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var text = ToolSchemas.GetString(args, "text");
        if (text is null)
            return Task.FromResult(ToolResult.Fail("text 가 비어 있습니다."));

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.FromResult(ToolResult.Fail("Dispatcher 를 사용할 수 없습니다 (UI 컨텍스트 없음). 클립보드 접근 불가."));

        try
        {
            // Clipboard 는 STA 스레드에서만 동작 — UI Dispatcher 로 마샬링.
            dispatcher.Invoke(() => Clipboard.SetText(text));
            return Task.FromResult(ToolResult.Json(new { written = true, length = text.Length }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"클립보드 쓰기 실패: {ex.Message}"));
        }
    }
}
