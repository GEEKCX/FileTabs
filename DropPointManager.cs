using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

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

    private void EnsureTray()
    {
        if (_tray is not null) return;
        _trayIcon = LoadTrayIcon();
        _trayMenu = BuildTrayMenu();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "FileTabs 拖放中转",
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _tray.MouseClick += (_, args) =>
        {
            // Clicking the tray icon follows the cursor's monitor, matching
            // the interaction users expect from the hot-key path.
            if (args.Button == System.Windows.Forms.MouseButtons.Left) ShowPadAtCursor();
        };
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("新建托盘点", null, (_, _) => ShowPadAtCursor());
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 FileTabs", null, (_, _) => Application.Current?.Shutdown());
        return menu;
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
