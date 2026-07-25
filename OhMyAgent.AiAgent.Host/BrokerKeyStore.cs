using OhMyAgent.AiAgent.Client.Services;

namespace OhMyAgent.AiAgent.Host;

/// <summary>
/// <see cref="IBrokerKeyStore"/> 의 thread-safe 구현. lifecycle(등록 루프)이 write, 협업 도구와
/// A2aInboundAuthenticator 가 read 한다. 리스너 다중 요청과 heartbeat 루프가 동시에 접근하므로 lock.
/// </summary>
public sealed class BrokerKeyStore : IBrokerKeyStore
{
    private readonly object _gate = new();
    private string? _agentId;
    private string? _kid;
    private string? _pem;

    public string? AgentId { get { lock (_gate) return _agentId; } }

    public bool TryGetKey(out string kid, out string pem)
    {
        lock (_gate)
        {
            if (_kid is not null && _pem is not null)
            {
                kid = _kid;
                pem = _pem;
                return true;
            }
        }
        kid = string.Empty;
        pem = string.Empty;
        return false;
    }

    public void SetAgentId(string id)
    {
        lock (_gate) _agentId = id;
    }

    public void SetKey(string kid, string pem)
    {
        lock (_gate)
        {
            _kid = kid;
            _pem = pem;
        }
    }
}
