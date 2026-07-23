using System.Collections.Generic;
using System.Windows.Input;
using OhMyAgent.AiAgent.Client.Models;

// 네임스페이스를 Models 로 두어, HotkeySettings 를 참조하는 모든 Client 코드에서
// using 추가 없이 확장 메서드 ToDisplayString() 이 그대로 보이게 한다(호출부 무수정).
namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// <see cref="HotkeySettings"/> 표시 문자열 생성 헬퍼(Client 전용). Core 로 이동한 순수 모델은
/// int KeyCode 만 보유하며, WPF <see cref="Key"/> 로의 해석은 UI 계층인 여기서 담당한다.
/// </summary>
public static class HotkeyDisplay
{
    /// <summary>표시 텍스트 생성 (예: "Ctrl + Space").</summary>
    public static string ToDisplayString(this HotkeySettings settings)
    {
        var parts = new List<string>();
        if (settings.Modifiers.HasFlag(HotkeyModifiers.Ctrl))  parts.Add("Ctrl");
        if (settings.Modifiers.HasFlag(HotkeyModifiers.Alt))   parts.Add("Alt");
        if (settings.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (settings.Modifiers.HasFlag(HotkeyModifiers.Win))   parts.Add("Win");
        parts.Add(KeyName(settings.KeyCode));
        return string.Join(" + ", parts);
    }

    /// <summary>가상 키 코드를 WPF <see cref="Key"/> 이름으로 변환.</summary>
    public static string KeyName(int keyCode) => ((Key)keyCode).ToString();
}
