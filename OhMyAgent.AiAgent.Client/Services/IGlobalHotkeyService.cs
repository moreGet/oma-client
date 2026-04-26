using System;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public interface IGlobalHotkeyService : IDisposable
{
    /// 핸들 확보 시점 1회 호출. WM_HOTKEY 후크를 건다.
    void Initialize(IntPtr windowHandle);

    /// 이미 등록 시 먼저 해제 후 재등록한다.
    bool Register(HotkeySettings settings);

    void Unregister();

    /// 등록된 핫키 발화 시 호출.
    event EventHandler? HotkeyPressed;
}
