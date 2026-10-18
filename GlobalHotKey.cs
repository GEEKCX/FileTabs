using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FileTabs;

/// <summary>
/// Registers a global hot-key (Shift+CapsLock) and raises Pressed on the
/// thread that owns the bound window. The window must live at least as long
/// as the registration; App uses its main window.
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkCapsLock = 0x14;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private bool _disposed;

    private GlobalHotKey(IntPtr hwnd, HwndSource source)
    {
        _hwnd = hwnd;
        _source = source;
        _source.AddHook(HandleMessage);
    }

    /// <summary>Bind to the app's main window; returns null if the combination is taken.</summary>
    public static GlobalHotKey? TryRegister(Window window)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(window);
        if (source is null)
        {
            AppStorage.Log("DropPoint hotkey: main window has no HwndSource.");
            return null;
        }
        if (!RegisterHotKey(source.Handle, HotKeyId, ModShift | ModNoRepeat, VkCapsLock))
        {
            AppStorage.Log($"DropPoint hotkey: RegisterHotKey failed, win32={Marshal.GetLastWin32Error()}.");
            return null;
        }
        AppStorage.Log("DropPoint hotkey registered.");
        return new GlobalHotKey(source.Handle, source);
    }

    public const int HotKeyId = 0xC0DE;

    public event Action? Pressed;

    private IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotKey || wParam.ToInt64() != HotKeyId) return IntPtr.Zero;
        Pressed?.Invoke();
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(HandleMessage);
        UnregisterHotKey(_hwnd, HotKeyId);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
