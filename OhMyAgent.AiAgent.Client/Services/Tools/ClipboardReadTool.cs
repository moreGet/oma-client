using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class ClipboardReadTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{}}
        """);

    public string Name => "clipboard_read";
    public string Description => "Read the current text contents of the Windows clipboard.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.FromResult(ToolResult.Fail("Dispatcher 를 사용할 수 없습니다 (UI 컨텍스트 없음). 클립보드 접근 불가."));

        try
        {
            // Clipboard 는 STA 스레드에서만 동작 — UI Dispatcher 로 마샬링.
            var text = dispatcher.Invoke(() =>
                Clipboard.ContainsText() ? Clipboard.GetText() : null);

            if (text is null)
                return Task.FromResult(ToolResult.Json(new { has_text = false, text = (string?)null }));

            return Task.FromResult(ToolResult.Json(new { has_text = true, text }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"클립보드 읽기 실패: {ex.Message}"));
        }
    }
}
