using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

public class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int WM_HOTKEY              = 0x0312;
    public  const int HOTKEY_ID_TOGGLE_CHAT  = 0xB001;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr      _windowHandle = IntPtr.Zero;
    private HwndSource? _hwndSource;
    private bool        _isHookAdded;
    private bool        _isRegistered;
    private bool        _disposed;

    public event EventHandler? HotkeyPressed;

    public void Initialize(IntPtr windowHandle)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("Invalid HWND.", nameof(windowHandle));

        _windowHandle = windowHandle;

        _hwndSource = HwndSource.FromHwnd(windowHandle);
        if (_hwndSource != null && !_isHookAdded)
        {
            _hwndSource.AddHook(WndProc);
            _isHookAdded = true;
        }
    }

    public bool Register(HotkeySettings settings)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        if (_windowHandle == IntPtr.Zero)
        {
            Debug.WriteLine("[GlobalHotkeyService] Register failed: Initialize was not called.");
            return false;
        }

        // 이미 등록돼 있으면 먼저 해제
        if (_isRegistered)
            Unregister();

        var fsModifiers = (uint)settings.Modifiers;
        var key         = (Key)settings.KeyCode;
        var vk          = (uint)KeyInterop.VirtualKeyFromKey(key);

        var ok = RegisterHotKey(_windowHandle, HOTKEY_ID_TOGGLE_CHAT, fsModifiers, vk);
        if (ok)
        {
            _isRegistered = true;
        }
        else
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"[GlobalHotkeyService] RegisterHotKey failed (Win32 error {error}).");
        }

        return ok;
    }

    public void Unregister()
    {
        if (!_isRegistered || _windowHandle == IntPtr.Zero) return;

        UnregisterHotKey(_windowHandle, HOTKEY_ID_TOGGLE_CHAT);
        _isRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_TOGGLE_CHAT)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Unregister(); } catch { /* swallow */ }

        if (_hwndSource != null && _isHookAdded)
        {
            try { _hwndSource.RemoveHook(WndProc); } catch { /* swallow */ }
            _isHookAdded = false;
        }

        _hwndSource   = null;
        _windowHandle = IntPtr.Zero;

        GC.SuppressFinalize(this);
    }
}
