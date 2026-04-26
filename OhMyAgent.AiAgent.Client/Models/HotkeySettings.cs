using System.Collections.Generic;

namespace OhMyAgent.AiAgent.Client.Models;

public class HotkeySettings
{
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Ctrl;
    public int KeyCode { get; set; } = (int)System.Windows.Input.Key.Space;

    public static HotkeySettings Default => new();

    /// 표시 텍스트 생성 (예: "Ctrl + Space")
    public string ToDisplayString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))  parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))   parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win))   parts.Add("Win");
        parts.Add(((System.Windows.Input.Key)KeyCode).ToString());
        return string.Join(" + ", parts);
    }
}
