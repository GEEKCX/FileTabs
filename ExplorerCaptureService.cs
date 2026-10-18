using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FileTabs;

/// <summary>
/// Native Explorer takeover: watch for Explorer windows as they are shown and
/// divert them into FileTabs. Uses EVENT_OBJECT_SHOW rather than *_CREATE:
/// Windows does not raise a create event for the top-level CabinetWClass
/// window (only for its children), but it does raise a show event.
/// </summary>
public sealed class ExplorerCaptureService : IDisposable
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;
    private const string ExplorerWindowClass = "CabinetWClass";

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, long> _seenHwnds = new();
    // SetWinEventHook only stores the raw function pointer; without a strong
    // managed reference the delegate is collected and Windows calls freed
    // memory (failfast "callback on a garbage collected delegate").
    private readonly WinEventDelegate _winEventProcKeepAlive;
    private IntPtr _hook;
    private bool _capturing;

    public ExplorerCaptureService()
    {
        _winEventProcKeepAlive = WinEventProc;
    }

    /// <summary>
    /// Fired after a new Explorer window was hidden but before it is closed.
    /// The argument is a filesystem path or a shell parsing name.
    /// </summary>
    public event Action<int, string>? NewExplorerWindow;

    public bool IsCapturing => Volatile.Read(ref _capturing);

    public void Start()
    {
        lock (_gate)
        {
            if (_capturing) return;
            _capturing = true;
            MarkExistingWindowsAsSeen();
            _hook = SetWinEventHook(
                EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventProcKeepAlive,
                idProcess: 0, idThread: 0,
                dwFlags: WINEVENT_OUTOFCONTEXT);
        }
        AppStorage.Log($"{nameof(ExplorerCaptureService)} started, hook={_hook}");
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_hook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
            _capturing = false;
        }
        AppStorage.Log($"{nameof(ExplorerCaptureService)} stopped.");
    }

    /// <summary>
    /// Pre-populate the seen set so restored/minimized pre-existing windows
    /// are never mistaken for newly created ones.
    /// </summary>
    private void MarkExistingWindowsAsSeen()
    {
        _ = EnumWindows((hwnd, _) =>
        {
            if (IsExplorerWindow(hwnd))
            {
                _seenHwnds[(int)hwnd] = Environment.TickCount64;
            }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Ask a captured (already hidden) Explorer window to close.</summary>
    public static void CloseWindow(int hwnd)
    {
        const uint WM_CLOSE = 0x0010;
        _ = PostMessage(new IntPtr(hwnd), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private void WinEventProc(
        IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
        uint threadId, uint time)
    {
        // Top-level window only; child controls raise their own show events.
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (!IsExplorerWindow(hwnd)) return;

        // First sighting wins; later show events (restore, spotlight) reuse it.
        var now = Environment.TickCount64;
        if (!_seenHwnds.TryAdd((int)hwnd, now)) return;
        if (_pruneCounter++ % 64 == 0) PruneSeenEntries(now);

        // Ctrl+Shift is the documented escape hatch: force a real window.
        if (IsKeyDown(VK_CONTROL) && IsKeyDown(VK_SHIFT)) return;

        // Hide immediately to avoid a visible flash while we resolve the path.
        _ = ShowWindow(hwnd, SW_HIDE);

        int captured = (int)hwnd;
        _ = Task.Run(() =>
        {
            string? path = null;
            try
            {
                path = ResolveExplorerPath(captured);
            }
            catch (Exception ex)
            {
                AppStorage.Log($"Capture resolve failed for {captured}: {ex}");
            }
            if (string.IsNullOrEmpty(path))
            {
                // Never leave a window hidden: hand it back on any failure.
                AppStorage.Log($"Capture gave back hwnd={captured} (no path).");
                _ = ShowWindow(new IntPtr(captured), SW_SHOW);
                _seenHwnds.TryRemove(captured, out _);
                return;
            }
            AppStorage.Log($"Captured hwnd={captured} path='{path}'.");
            NewExplorerWindow?.Invoke(captured, path);
        });
    }

    private long _pruneCounter;

    private void PruneSeenEntries(long now)
    {
        // Cheap size guard; entries are tiny so a loose cap is fine.
        foreach (var kv in _seenHwnds)
        {
            if (now - kv.Value > 300_000)
            {
                _ = _seenHwnds.TryRemove(kv.Key, out _);
            }
        }
    }

    private static bool IsKeyDown(int vk)
    {
        // High bit set = key currently down.
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    private static bool IsExplorerWindow(IntPtr hwnd)
    {
        // Top-level windows only; Explorer's children are DirectUIHWND etc.
        if (GetParent(hwnd) != IntPtr.Zero) return false;
        var sb = new System.Text.StringBuilder(32);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString().Equals(ExplorerWindowClass, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Find the Explorer window matching this hwnd through Shell.Application
    /// and read its current location. LocationURL covers filesystem folders;
    /// Document.Folder.Self.Path covers virtual folders (This PC, libraries).
    /// Retries briefly because the window may still be navigating.
    /// </summary>
    private static string? ResolveExplorerPath(int hwnd, int attempts = 12)
    {
        for (int i = 0; i < attempts; i++)
        {
            var path = TryResolveOnce(hwnd);
            if (!string.IsNullOrEmpty(path)) return path;
            Thread.Sleep(80);
        }
        return null;
    }

    private static string? TryResolveOnce(int hwnd)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic windows = shell.Windows();
            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? window = null;
                try { window = windows.Item(i); }
                catch (COMException) { continue; }
                if (window is null) continue;
                try
                {
                    if ((int)window.HWND != hwnd) continue;

                    var url = (string?)window.LocationURL;
                    if (!string.IsNullOrEmpty(url))
                    {
                        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                        {
                            return new Uri(url).LocalPath;
                        }
                        return url; // shell:: / ::{GUID} and friends.
                    }

                    // Virtual folders have an empty LocationURL; fall back to
                    // the shell path exposed by the folder item (e.g. "::{20D04FE0-...}").
                    // Late-bound property access can throw anything (the
                    // RuntimeBinderException is the usual suspect), so catch broadly here.
                    string? selfPath = null;
                    try { selfPath = (string?)window.Document.Folder.Self.Path; }
                    catch (Exception) { /* navigation still in flight */ }
                    return string.IsNullOrEmpty(selfPath) ? null : selfPath;
                }
                finally
                {
                    _ = Marshal.FinalReleaseComObject(window);
                }
            }
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(shell);
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hModWineventModifier,
        WinEventDelegate pfnwineventproc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;

    private delegate void WinEventDelegate(
        IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
        uint threadId, uint time);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public void Dispose() => Stop();
}
