using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// MCP 레이어 진입 서비스. SSE 서버를 호스팅하고 JSON-RPC 메서드를 라우팅한다.
/// </summary>
public class McpRemoteAgentService : IRemoteAgentService
{
    private static readonly McpTool[] Tools =
    {
        new McpTool
        {
            Name = "run_powershell",
            Description = "Execute a PowerShell script on the local Windows machine. Subject to security validation.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    script = new { type = "string", description = "PowerShell script body" },
                    timeoutMs = new { type = "integer", @default = 30000 }
                },
                required = new[] { "script" }
            }
        },
        new McpTool
        {
            Name = "run_cmd",
            Description = "Execute a Windows CMD command. Subject to security validation.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string" },
                    timeoutMs = new { type = "integer", @default = 30000 }
                },
                required = new[] { "command" }
            }
        }
    };

    private readonly ISettingsService _settings;
    private readonly IMcpSseServer _sseServer;
    private readonly IScriptExecutor _executor;

    private bool _isRunning;

    public bool IsRunning => _isRunning;
    public int Port => _settings.Current.McpPort;

    public event EventHandler<bool>? RunningStateChanged;

    public McpRemoteAgentService(
        ISettingsService settings,
        IMcpSseServer sseServer,
        IScriptExecutor executor)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sseServer = sseServer ?? throw new ArgumentNullException(nameof(sseServer));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return;

        _sseServer.RequestHandler = HandleRequestAsync;

        try
        {
            await _sseServer.StartAsync(_settings.Current.McpPort, ct).ConfigureAwait(false);
            _isRunning = true;
            RunningStateChanged?.Invoke(this, true);
            Debug.WriteLine($"[McpRemoteAgentService] started on port {_settings.Current.McpPort}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpRemoteAgentService] start failed: {ex.Message}");
            _isRunning = false;
            RunningStateChanged?.Invoke(this, false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        try
        {
            await _sseServer.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpRemoteAgentService] stop failed: {ex.Message}");
        }

        _isRunning = false;
        RunningStateChanged?.Invoke(this, false);
        Debug.WriteLine("[McpRemoteAgentService] stopped");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await _sseServer.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    // --- request routing ----------------------------------------------------

    private async Task<McpResponse> HandleRequestAsync(McpRequest request, CancellationToken ct)
    {
        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "tools/list" => HandleToolsList(request),
                "tools/call" => await HandleToolsCallAsync(request, ct).ConfigureAwait(false),
                "ping" => McpResponse.Ok(request.Id, new { }),
                _ => McpResponse.Fail(request.Id, McpError.MethodNotFound, $"Method '{request.Method}' not found")
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpRemoteAgentService] handler error: {ex}");
            return McpResponse.Fail(request.Id, McpError.InternalError, ex.Message);
        }
    }

    private static McpResponse HandleInitialize(McpRequest request)
    {
        var result = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { tools = new { } },
            serverInfo = new { name = "OhMyAgent", version = "1.0.0" }
        };
        return McpResponse.Ok(request.Id, result);
    }

    private static McpResponse HandleToolsList(McpRequest request)
    {
        return McpResponse.Ok(request.Id, new { tools = Tools });
    }

    private async Task<McpResponse> HandleToolsCallAsync(McpRequest request, CancellationToken ct)
    {
        if (request.Params == null)
            return McpResponse.Fail(request.Id, McpError.InvalidParams, "Missing params");

        var name = GetString(request.Params, "name");
        if (string.IsNullOrEmpty(name))
            return McpResponse.Fail(request.Id, McpError.InvalidParams, "Missing tool name");

        var arguments = GetObject(request.Params, "arguments");

        switch (name)
        {
            case "run_powershell":
                return await CallScriptToolAsync(request.Id, ScriptType.PowerShell, arguments, ct).ConfigureAwait(false);
            case "run_cmd":
                return await CallScriptToolAsync(request.Id, ScriptType.Cmd, arguments, ct).ConfigureAwait(false);
            default:
                return McpResponse.Fail(request.Id, McpError.MethodNotFound, $"Tool '{name}' not found");
        }
    }

    private async Task<McpResponse> CallScriptToolAsync(
        object? requestId,
        ScriptType scriptType,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        if (arguments == null)
            return McpResponse.Fail(requestId, McpError.InvalidParams, "Missing arguments");

        var scriptKey = scriptType == ScriptType.PowerShell ? "script" : "command";
        var script = GetString(arguments, scriptKey);
        if (string.IsNullOrWhiteSpace(script))
            return McpResponse.Fail(requestId, McpError.InvalidParams, $"Missing '{scriptKey}'");

        var timeoutMs = GetInt(arguments, "timeoutMs") ?? 30000;

        // 1. Security validation
        var validation = SecurityValidator.Validate(script, scriptType);
        if (!validation.IsValid)
        {
            return McpResponse.Fail(
                requestId,
                McpError.SecurityViolation,
                validation.Reason ?? "Security violation",
                new { matchedPattern = validation.MatchedPattern });
        }

        // 2. Execute
        ScriptResult result;
        try
        {
            result = scriptType == ScriptType.PowerShell
                ? await _executor.ExecutePowerShellAsync(script, timeoutMs, ct).ConfigureAwait(false)
                : await _executor.ExecuteCmdAsync(script, timeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return McpResponse.Fail(requestId, McpError.ExecutionFailed, ex.Message);
        }

        // 3. Map result
        if (result.TimedOut)
        {
            return McpResponse.Fail(
                requestId,
                McpError.ExecutionTimeout,
                "Execution timed out",
                new { result.Stdout, result.Stderr, result.DurationMs });
        }

        var text = !string.IsNullOrEmpty(result.Stdout)
            ? result.Stdout
            : result.Stderr;

        var content = new[]
        {
            new { type = "text", text }
        };

        return McpResponse.Ok(requestId, new
        {
            content,
            isError = !result.Success,
            exitCode = result.ExitCode,
            durationMs = result.DurationMs
        });
    }

    // --- params helpers -----------------------------------------------------

    private static string? GetString(IReadOnlyDictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var raw) || raw == null) return null;
        return raw switch
        {
            string s => s,
            JValue jv => jv.ToString(),
            _ => raw.ToString()
        };
    }

    private static int? GetInt(IReadOnlyDictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var raw) || raw == null) return null;
        return raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JValue jv when jv.Type == JTokenType.Integer => jv.Value<int>(),
            JValue jv when jv.Type == JTokenType.Float => (int)jv.Value<double>(),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    private static IReadOnlyDictionary<string, object?>? GetObject(IReadOnlyDictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var raw) || raw == null) return null;
        switch (raw)
        {
            case IDictionary<string, object?> d:
                return new Dictionary<string, object?>(d);
            case JObject jo:
                return jo.Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
            default:
                return null;
        }
    }
}
