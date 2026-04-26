using System.ComponentModel;
using System.IO;
using Microsoft.SemanticKernel;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class AgentActionService : IAgentActionService
{
    private readonly Kernel _kernel;

    public AgentActionService()
    {
        _kernel = Kernel.CreateBuilder().Build();
        _kernel.ImportPluginFromObject(new FileCreationPlugin(), "FileTools");
    }

    public async Task<string> ExecuteCreateFileAsync(string content, CancellationToken ct = default)
    {
        var cleanContent = content
            .Replace("[ACTION:CREATE_FILE]", string.Empty)
            .Trim();

        var result = await _kernel.InvokeAsync(
            "FileTools",
            "create_report_file",
            new KernelArguments { ["content"] = cleanContent },
            ct);

        return result.ToString();
    }
}

// Semantic Kernel 플러그인 — 바탕화면에 Agent_Report.txt 생성
file sealed class FileCreationPlugin
{
    [KernelFunction("create_report_file")]
    [Description("Writes AI agent output to Agent_Report.txt on the user's desktop")]
    public async Task<string> CreateReportFileAsync(
        [Description("Report content to write")] string content)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var path    = Path.Combine(desktop, "Agent_Report.txt");

        await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
        return path;
    }
}
