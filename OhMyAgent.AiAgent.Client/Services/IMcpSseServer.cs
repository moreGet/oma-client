using System;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IMcpSseServer : IAsyncDisposable
{
    bool IsListening { get; }
    int Port { get; }

    /// <summary>
    /// JSON-RPC 요청 처리 핸들러. McpRemoteAgentService가 등록.
    /// </summary>
    Func<McpRequest, CancellationToken, Task<McpResponse>>? RequestHandler { get; set; }

    Task StartAsync(int port, CancellationToken ct = default);
    Task StopAsync();
    Task BroadcastAsync(McpResponse response, CancellationToken ct = default);
}
