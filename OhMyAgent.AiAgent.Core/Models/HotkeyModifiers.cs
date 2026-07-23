using System;

namespace OhMyAgent.AiAgent.Client.Models;

[Flags]
public enum HotkeyModifiers
{
    None  = 0x0,
    Alt   = 0x1,
    Ctrl  = 0x2,
    Shift = 0x4,
    Win   = 0x8,
}
