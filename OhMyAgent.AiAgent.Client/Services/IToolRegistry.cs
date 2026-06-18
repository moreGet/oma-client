using System.Collections.Generic;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IToolRegistry
{
    IReadOnlyList<ITool> All { get; }

    /// <summary>AgentRequest.Tools 생성용.</summary>
    IReadOnlyList<ToolSchema> ToSchemas();

    bool TryGet(string name, out ITool tool);
}
