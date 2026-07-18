using System;
using WpfClipboard = System.Windows.Clipboard;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 클립보드 안전 래퍼. System.Windows.Clipboard 는 다른 프로세스가 클립보드를 점유하면 예외를 던진다
/// (CLIPBRD_E_CANT_OPEN). 복사 버튼 하나 때문에 앱이 죽으면 안 되므로 삼킨다.
///
/// 반드시 UI(STA) 스레드에서 호출해야 한다 — 복사 커맨드는 버튼에서 실행되므로 이미 UI 스레드다.
/// </summary>
public static class ClipboardSafe
{
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            WpfClipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clipboard", "복사 실패(클립보드 점유 등)", ex);
            return false;
        }
    }
}
