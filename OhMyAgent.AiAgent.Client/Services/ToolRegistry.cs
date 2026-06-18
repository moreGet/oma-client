using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _byName;
    private readonly IReadOnlyList<ITool> _all;
    private readonly IReadOnlyList<ToolSchema> _schemas;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _all = tools.ToList();
        _byName = _all.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _schemas = _all.Select(t => new ToolSchema(t.Name, t.Description, t.ParametersSchema)).ToList();
    }

    public IReadOnlyList<ITool> All => _all;

    public IReadOnlyList<ToolSchema> ToSchemas() => _schemas;

    public bool TryGet(string name, [MaybeNullWhen(false)] out ITool tool)
        => _byName.TryGetValue(name, out tool);
}
