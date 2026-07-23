using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Host;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

public class A2aSseWriterTests
{
    private static async Task<string> WriteAsync(AgentEvent ev, A2aOptions? opts = null)
    {
        using var ms = new MemoryStream();
        var writer = new A2aSseWriter(ms, "run1", "model-x", opts ?? new A2aOptions());
        await writer.WriteAsync(ev, CancellationToken.None);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static JsonElement EmptyArgs()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task TextDelta_maps_to_content_delta_frame()
    {
        var frame = await WriteAsync(new AgentTextDelta("hi"));
        Assert.Equal("event: content_delta\ndata: {\"delta\":\"hi\"}\n\n", frame);
    }

    [Fact]
    public async Task Done_maps_to_message_stop_with_end_turn_and_usage_fields()
    {
        var frame = await WriteAsync(new AgentDone("hi", new Usage(3, 5, 8)));

        Assert.StartsWith("event: message_stop\ndata: ", frame);
        Assert.Contains("\"stop_reason\":\"end_turn\"", frame);
        Assert.Contains("\"prompt_tokens\":3", frame);
        Assert.Contains("\"completion_tokens\":5", frame);
        Assert.Contains("\"total_tokens\":8", frame);
        Assert.EndsWith("\n\n", frame);
    }

    [Fact]
    public async Task Error_maps_to_nested_envelope()
    {
        var frame = await WriteAsync(new AgentError("x", "y"));
        Assert.Equal("event: error\ndata: {\"error\":{\"code\":\"x\",\"message\":\"y\"}}\n\n", frame);
    }

    [Fact]
    public async Task ToolCall_is_suppressed()
    {
        var frame = await WriteAsync(new AgentToolCallStarted("c1", "read_file", EmptyArgs(), ToolRisk.ReadOnly));
        Assert.Equal("", frame);
    }

    [Fact]
    public async Task IterationAdvanced_is_suppressed()
    {
        var frame = await WriteAsync(new AgentIterationAdvanced(2, 25));
        Assert.Equal("", frame);
    }

    [Fact]
    public async Task ThinkingDelta_suppressed_by_default()
    {
        var frame = await WriteAsync(new AgentThinkingDelta("secret"));
        Assert.Equal("", frame);
    }

    [Fact]
    public async Task ThinkingDelta_emitted_when_exposed()
    {
        var frame = await WriteAsync(new AgentThinkingDelta("t"),
            new A2aOptions { ExposeThinking = true });
        Assert.Equal("event: thinking_delta\ndata: {\"delta\":\"t\"}\n\n", frame);
    }

    [Fact]
    public async Task Special_characters_are_json_escaped()
    {
        var frame = await WriteAsync(new AgentTextDelta("a\"b\n"));
        Assert.Contains("\"delta\":\"a\\\"b\\n\"", frame);
    }

    [Fact]
    public async Task MessageStart_advertises_run_and_model()
    {
        using var ms = new MemoryStream();
        var writer = new A2aSseWriter(ms, "run1", "model-x", new A2aOptions());
        await writer.WriteMessageStartAsync(CancellationToken.None);
        var frame = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Equal("event: message_start\ndata: {\"id\":\"run1\",\"model\":\"model-x\"}\n\n", frame);
    }
}
