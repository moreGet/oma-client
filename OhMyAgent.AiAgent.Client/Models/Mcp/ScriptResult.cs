namespace OhMyAgent.AiAgent.Client.Models.Mcp;

public class ScriptResult
{
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public bool Success => ExitCode == 0;
    public long DurationMs { get; set; }
    public bool TimedOut { get; set; }
}
