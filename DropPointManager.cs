using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using WpfWindow = System.Windows.Window;

namespace FileTabs;

/// <summary>
/// Owns the drop-point feature: tray icon, global hot-key and the pool of
/// temporary staging windows. All entry points must be invoked on the UI
/// thread; the hot-key hook already marshals through App.Dispatcher.
/// </summary>
public sealed class DropPointManager : IDisposable
{
    private const int MaxPads = 3;

    private sealed record PadPlacement(string MonitorDeviceName, int Slot);

    private readonly List<DropPointWindow> _pads = new();
    private readonly Dictionary<DropPointWindow, PadPlacement> _placements = new();
    private readonly Dictionary<(string MonitorDeviceName, int Slot), System.Windows.Point>
        _sessionPositions = new();
    private NotifyIcon? _tray;
    private ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _trayIcon;
    private GlobalHotKey? _hotKey;
    private Window? _mainWindow;
    private bool _disposed;
    // 静默启动钉住的托盘：不随拖放中转开关一起移除。
    private bool _trayPinned;

    public void Start(Window mainWindow)
    {
        if (_disposed) return;
        _mainWindow = mainWindow;
        EnsureTray();
        RegisterHotKey();
    }

    public void ApplySettings()
    {
        if (_disposed) return;
        _mainWindow ??= Application.Current?.MainWindow;
        if (AppSettings.Current.DropPointEnabled)
        {
            EnsureTray();
            RegisterHotKey();
        }
        else
        {
            UnregisterHotKey();
            CloseAllPads();
            // 静默启动时托盘是找回窗口的唯一入口，不能因为关掉拖放中转就消失。
            if (!_trayPinned)
            {
                RemoveTray();
            }
        }
        RebuildTrayMenu();
    }

    /// <summary>
    /// 创建常驻托盘：无论是否静默启动、是否开启拖放中转都要有，
    /// 否则窗口关掉后用户既找不回程序也退不出去。只创建一次。
    /// </summary>
    public void EnsurePersistentTray()
    {
        if (_disposed) return;
        _mainWindow ??= Application.Current?.MainWindow;
        _trayPinned = true;
        EnsureTray();
    }

    /// <summary>
    /// 托盘「退出」：先让窗口走一次正常的关闭流程，把会话和窗口位置存下来。
    /// Application.Shutdown() 不触发 Window.Closing，单用它会把这两项丢掉。
    /// </summary>
    private void ExitApplication()
    {
        if (_mainWindow is WpfWindow window)
        {
            window.Close();
            return;
        }
        Application.Current?.Shutdown();
    }

    private void RemoveTray()
    {
        if (_tray is { } tray)
        {
            tray.Visible = false;
            tray.ContextMenuStrip = null;
            tray.Dispose();
            _tray = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void EnsureTray()
    {
        if (_tray is not null) return;
        _trayIcon = LoadTrayIcon();
        _trayMenu = BuildTrayMenu();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            // 未开拖放中转时托盘只负责找回主窗口，文案不能还写“拖放中转”。
            Text = AppSettings.Current.DropPointEnabled ? "FileTabs 拖放中转" : "FileTabs",
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _tray.MouseClick += (_, args) =>
        {
            // 静默启动后窗口可能从未显示过，托盘就是唯一的找回入口：
            // 左键先确保主窗口可见；开了拖放中转再按光标弹托盘点。
            if (args.Button != System.Windows.Forms.MouseButtons.Left) return;
            ShowMainWindow();
            if (AppSettings.Current.DropPointEnabled) ShowPadAtCursor();
        };
    }

    /// <summary>把主窗口从隐藏状态叫回来（静默启动后托盘/热键路径共用）。</summary>
    private void ShowMainWindow()
    {
        if (_mainWindow is not { } window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == System.Windows.WindowState.Minimized)
        {
            window.WindowState = System.Windows.WindowState.Normal;
        }
        window.Activate();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
        if (AppSettings.Current.DropPointEnabled)
        {
            menu.Items.Add("新建托盘点", null, (_, _) => ShowPadAtCursor());
        }
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 FileTabs", null, (_, _) => ExitApplication());
        return menu;
    }

    /// <summary>菜单项依赖拖放中转开关，开关变化后重建。</summary>
    private void RebuildTrayMenu()
    {
        if (_tray is null) return;
        var old = _trayMenu;
        _trayMenu = BuildTrayMenu();
        _tray.ContextMenuStrip = _trayMenu;
        _tray.Text = AppSettings.Current.DropPointEnabled ? "FileTabs 拖放中转" : "FileTabs";
        old?.Dispose();
    }

    private void OpenSettings()
    {
        if (_mainWindow is not MainWindow main) return;
        var settings = new SettingsWindow { Owner = main };
        settings.ShowDialog();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(exe)
                       ?? System.Drawing.SystemIcons.Application;
            }
        }
        catch (Exception)
        {
            // Fall through to the stock icon; a tray icon must not block startup.
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void RegisterHotKey()
    {
        if (_hotKey is not null) return;
        _hotKey = _mainWindow is { } window ? GlobalHotKey.TryRegister(window) : null;
        if (_hotKey is not null) _hotKey.Pressed += OnHotKey;
    }

    private void UnregisterHotKey()
    {
        _hotKey?.Dispose();
        _hotKey = null;
    }

    public void OnHotKey()
    {
        if (!AppSettings.Current.DropPointEnabled) return;
        ShowPadAtCursor();
    }

    /// <summary>Open a pad on the monitor containing FileTabs.</summary>
    public void ShowPadForMainWindow()
    {
        if (!AppSettings.Current.DropPointEnabled) return;
        var monitor = _mainWindow is { } window
            ? DropPointWindow.GetMonitorForWindow(window)
            : DropPointWindow.GetMonitorForCursor();
        ShowOrCreatePad(monitor);
    }

    /// <summary>Open a pad on the monitor containing the mouse cursor.</summary>
    public void ShowPadAtCursor()
    {
        if (!AppSettings.Current.DropPointEnabled) return;
        ShowOrCreatePad(DropPointWindow.GetMonitorForCursor());
    }

    private void ShowOrCreatePad(IntPtr monitor)
    {
        if (!AppSettings.Current.DropPointEnabled) return;
        var monitorDevice = DropPointWindow.GetMonitorDeviceName(monitor);

        // Reuse an empty pad before creating another one. This keeps repeated
        // hot-key presses from leaving a trail of identical empty frames.
        var reusable = _pads.FirstOrDefault(pad => !pad.HasItems);
        if (reusable is not null)
        {
            TouchPad(reusable);
            reusable.BringToFront();
            AppStorage.Log($"DropPoint pad: reused existing pad at {reusable.Left},{reusable.Top}.");
            return;
        }

        if (_pads.Count >= MaxPads)
        {
            var mostRecent = _pads[^1];
            mostRecent.BringToFront();
            AppStorage.Log($"DropPoint pad: limit {MaxPads} reached; raised existing pad.");
            return;
        }

        AppStorage.Log("DropPoint pad: creating.");
        var pad = new DropPointWindow();
        _pads.Add(pad);
        pad.Closed += (_, _) => ForgetPad(pad);
        pad.Moved += (_, _) => RememberPadPosition(pad);

        var slotIndex = NextSlotIndex(monitorDevice);
        pad.PlaceAtRightCenter(monitor, slotIndex);
        if (_sessionPositions.TryGetValue((monitorDevice, slotIndex), out var savedPosition))
        {
            pad.PlaceAtSessionPosition(monitor, savedPosition.X, savedPosition.Y);
        }
        _placements[pad] = new PadPlacement(monitorDevice, slotIndex);
        pad.Show();
        pad.BringToFront();
        AppStorage.Log(
            $"DropPoint pad shown at {pad.Left},{pad.Top}; monitor={monitorDevice} " +
            $"slot={slotIndex}; visible={pad.IsVisible} loaded={pad.IsLoaded}.");
    }

    private int NextSlotIndex(string monitorDevice)
    {
        var usedSlots = _placements.Values
            .Where(placement => string.Equals(
                placement.MonitorDeviceName, monitorDevice, StringComparison.OrdinalIgnoreCase))
            .Select(placement => placement.Slot)
            .ToHashSet();
        for (var slot = 0; slot < MaxPads; slot++)
        {
            if (!usedSlots.Contains(slot)) return slot;
        }
        return MaxPads - 1;
    }

    private void TouchPad(DropPointWindow pad)
    {
        var index = _pads.IndexOf(pad);
        if (index < 0 || index == _pads.Count - 1) return;
        _pads.RemoveAt(index);
        _pads.Add(pad);
    }

    private void RememberPadPosition(DropPointWindow pad)
    {
        if (!_placements.TryGetValue(pad, out var previous)) return;
        var monitor = DropPointWindow.GetMonitorForWindow(pad);
        var monitorDevice = DropPointWindow.GetMonitorDeviceName(monitor);
        if (!string.Equals(
                previous.MonitorDeviceName, monitorDevice, StringComparison.OrdinalIgnoreCase))
        {
            _sessionPositions.Remove((previous.MonitorDeviceName, previous.Slot));
        }
        _sessionPositions[(monitorDevice, previous.Slot)] =
            new System.Windows.Point(pad.Left, pad.Top);
        _placements[pad] = new PadPlacement(monitorDevice, previous.Slot);
    }

    private void ForgetPad(DropPointWindow pad)
    {
        _pads.Remove(pad);
        _placements.Remove(pad);
    }

    private void CloseAllPads()
    {
        foreach (var pad in _pads.ToArray())
        {
            try { pad.Close(); }
            catch (Exception)
            {
                // A pad may already be closing while an OLE drag is ending.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseAllPads();
        UnregisterHotKey();
        if (_tray is { } tray)
        {
            tray.Visible = false;
            tray.ContextMenuStrip = null;
            tray.Dispose();
            _tray = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
