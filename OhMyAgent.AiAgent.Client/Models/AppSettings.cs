namespace OhMyAgent.AiAgent.Client.Models;

public class AppSettings
{
    public HotkeySettings Hotkey { get; set; } = HotkeySettings.Default;
    public double Opacity { get; set; } = 1.0;
    public int SchemaVersion { get; set; } = 2;
    public int McpPort { get; set; } = 3000;
    public bool McpEnabled { get; set; } = true;
}
