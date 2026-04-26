using System;
using System.Threading;
using System.Threading.Tasks;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IRemoteAgentService : IAsyncDisposable
{
    bool IsRunning { get; }
    int Port { get; }

    event EventHandler<bool>? RunningStateChanged;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
