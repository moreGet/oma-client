namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 전역 핫키 설정(순수 데이터). WPF 의존을 제거하고 KeyCode 를 <c>System.Windows.Input.Key</c> 서수(int)로만 보유한다.
/// (Win32 가상 키 코드가 아님 — Client 소비처가 <c>(Key)KeyCode</c> 로 왕복 변환한다.)
/// 표시 문자열은 Client 측 <c>HotkeyDisplay.ToDisplayString()</c> 확장 메서드가 담당한다.
/// </summary>
public class HotkeySettings
{
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Ctrl;

    /// <summary>WPF <c>Key</c> enum 서수. 기본값 18 = <c>Key.Space</c>.</summary>
    public int KeyCode { get; set; } = 18 /* (int)Key.Space */;

    public static HotkeySettings Default => new();
}
