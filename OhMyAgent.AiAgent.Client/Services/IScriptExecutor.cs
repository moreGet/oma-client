using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IScriptExecutor
{
    Task<ScriptResult> ExecutePowerShellAsync(string script, int timeoutMs = 30000, string? workingDirectory = null, CancellationToken ct = default);
    Task<ScriptResult> ExecuteCmdAsync(string command, int timeoutMs = 30000, string? workingDirectory = null, CancellationToken ct = default);
}
