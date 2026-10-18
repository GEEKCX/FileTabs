using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FileTabs;

/// <summary>
/// Temporary drag-and-drop staging pad: accept one or more file-system items
/// from an arbitrary drag source, then hand them back out as a standard
/// CF_HDROP once the user drags away from the pad.
/// </summary>
public sealed class DropPointWindow : Window
{
    private const double PadSize = 190;
    private const double CornerRadius = 14;

    private readonly List<string> _paths = new();
    private readonly Border _root;
    private readonly TextBlock _primaryText;
    private readonly TextBlock _secondaryText;
    private readonly TextBlock _countBadge;
    private readonly FrameworkElement _iconHost;
    private readonly Image _fileIcon;
    private readonly Border _countBadgeBorder;
    private bool _isMoving;
    private NativePoint _moveStartCursor;
    private double _moveStartLeft;
    private double _moveStartTop;
    private double _moveScale = 1;

    public event EventHandler? Moved;

    public bool HasItems => _paths.Count > 0;

    public DropPointWindow()
    {
        Title = "FileTabs 拖放中转";
        Width = PadSize;
        Height = PadSize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        UseLayoutRounding = true;
        AllowDrop = true;

        _iconHost = BuildIconHost();
        _primaryText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 10, 12, 0),
            Foreground = (Brush)FindResourceOrDefault("TextFillColorPrimaryBrush", Brushes.Gray),
            Text = "拖放文件到此",
        };
        _secondaryText = new TextBlock
        {
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(12, 4, 12, 10),
            Foreground = (Brush)FindResourceOrDefault("TextFillColorSecondaryBrush", Brushes.Gray),
            Text = "点击 × 关闭",
        };
        _countBadge = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "0",
        };
        _countBadgeBorder = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = (Brush)FindResourceOrDefault("AccentFillColorDefaultBrush", Brushes.SteelBlue),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2),
            Child = _countBadge,
            Visibility = Visibility.Collapsed,
        };
        _fileIcon = new Image
        {
            Width = 38,
            Height = 38,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
        };

        var iconContainer = new Grid
        {
            Width = 64,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0),
        };
        iconContainer.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = (Brush)FindResourceOrDefault("SubtleFillColorSecondaryBrush", Brushes.WhiteSmoke),
        });
        iconContainer.Children.Add(_iconHost);
        iconContainer.Children.Add(_fileIcon);
        iconContainer.Children.Add(_countBadgeBorder);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(iconContainer);
        stack.Children.Add(_primaryText);
        stack.Children.Add(_secondaryText);

        var closeButton = new TextBlock
        {
            Text = "\uE711",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 20,
            Height = 20,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Foreground = (Brush)FindResourceOrDefault("TextFillColorSecondaryBrush", Brushes.Gray),
            ToolTip = "关闭",
        };
        closeButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        closeButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseSafe();
        };
        closeButton.MouseEnter += (_, _) => closeButton.Foreground =
            (Brush)FindResourceOrDefault("TextFillColorPrimaryBrush", Brushes.Black);
        closeButton.MouseLeave += (_, _) => closeButton.Foreground =
            (Brush)FindResourceOrDefault("TextFillColorSecondaryBrush", Brushes.Gray);

        var closeHolder = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Background = Brushes.Transparent,
            Child = closeButton,
        };

        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(BuildDragHandle());
        grid.Children.Add(closeHolder);

        _root = new Border
        {
            CornerRadius = new CornerRadius(CornerRadius),
            Background = (Brush)FindResourceOrDefault("SolidBackgroundFillColorBaseBrush", Brushes.White),
            BorderBrush = (Brush)FindResourceOrDefault("CardStrokeColorDefaultBrush", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Child = grid,
        };

        Content = _root;
        Drop += OnDropped;
        DragEnter += (_, _) => SetDragOver(true);
        DragLeave += (_, _) => SetDragOver(false);
        PreviewDragOver += OnDragOver;
        MouseLeftButtonDown += OnPadMouseDown;
        MouseMove += (_, e) =>
        {
            if (_paths.Count == 0 && UpdateMove(e.LeftButton == MouseButtonState.Pressed)) e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_paths.Count == 0 && EndMove(this)) e.Handled = true;
        };
        LostMouseCapture += (_, _) => _isMoving = false;
        SourceInitialized += (_, _) => ApplyNoActivateStyle();
    }

    private Border BuildDragHandle()
    {
        var grip = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        for (var i = 0; i < 3; i++)
        {
            grip.Children.Add(new Border
            {
                Width = 14,
                Height = 1,
                Margin = new Thickness(0, 1, 0, 1),
                Background = (Brush)FindResourceOrDefault("TextFillColorTertiaryBrush", Brushes.Gray),
            });
        }

        var handle = new Border
        {
            Height = 24,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            ToolTip = "拖动以移动托盘点",
            Child = grip,
        };
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            BeginMove(handle);
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (!UpdateMove(e.LeftButton == MouseButtonState.Pressed)) return;
            e.Handled = true;
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (!EndMove(handle)) return;
            e.Handled = true;
        };
        handle.LostMouseCapture += (_, _) => _isMoving = false;
        handle.MouseEnter += (_, _) => handle.Background =
            (Brush)FindResourceOrDefault("SubtleFillColorSecondaryBrush", Brushes.WhiteSmoke);
        handle.MouseLeave += (_, _) =>
        {
            if (!_isMoving) handle.Background = Brushes.Transparent;
        };
        return handle;
    }

    public static IntPtr GetMonitorForWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return MonitorFromWindow(hwnd, MonitorDefaultToNearest);
    }

    public static IntPtr GetMonitorForCursor()
    {
        return GetCursorPos(out var cursor)
            ? MonitorFromPoint(cursor, MonitorDefaultToNearest)
            : MonitorFromPoint(default, MonitorDefaultToPrimary);
    }

    public static string GetMonitorDeviceName(IntPtr monitor)
    {
        var info = MonitorInfoW.Create();
        if (monitor != IntPtr.Zero &&
            GetMonitorInfo(monitor, ref info) &&
            !string.IsNullOrWhiteSpace(info.SzDevice))
        {
            return info.SzDevice;
        }
        return monitor.ToString();
    }

    /// <summary>
    /// Place a newly created pad at the right edge of the monitor, vertically
    /// centered. Additional pads step upward so their close buttons stay
    /// reachable without covering each other completely.
    /// </summary>
    public void PlaceAtRightCenter(IntPtr monitor, int slotIndex)
    {
        if (!TryGetMonitorMetrics(monitor, out var info, out var scale)) return;

        var widthPx = (int)Math.Round(PadSize * scale);
        var marginPx = (int)Math.Round(24 * scale);
        var offsetPx = (int)Math.Round(12 * scale);
        var x = info.RcWork.Right - marginPx - widthPx;
        var y = info.RcWork.Top +
                (info.RcWork.Bottom - info.RcWork.Top - widthPx) / 2 -
                Math.Max(0, slotIndex) * offsetPx;

        var minX = info.RcWork.Left;
        var maxX = Math.Max(minX, info.RcWork.Right - widthPx);
        var minY = info.RcWork.Top;
        var maxY = Math.Max(minY, info.RcWork.Bottom - widthPx);
        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        // WPF window coordinates are device-independent; convert with the
        // effective DPI of the monitor the pad will appear on.
        Left = x / scale;
        Top = y / scale;
    }

    /// <summary>Restore a position remembered earlier in this process.</summary>
    public void PlaceAtSessionPosition(IntPtr monitor, double left, double top)
    {
        if (!TryGetMonitorMetrics(monitor, out var info, out var scale)) return;

        var minLeft = info.RcWork.Left / scale;
        var minTop = info.RcWork.Top / scale;
        var maxLeft = Math.Max(minLeft, info.RcWork.Right / scale - Width);
        var maxTop = Math.Max(minTop, info.RcWork.Bottom / scale - Height);
        Left = Math.Clamp(left, minLeft, maxLeft);
        Top = Math.Clamp(top, minTop, maxTop);
    }

    /// <summary>
    /// Raise the pad without activating it. The window is WS_EX_NOACTIVATE,
    /// so Activate() is intentionally not used here.
    /// </summary>
    public void BringToFront()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _ = SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static bool TryGetMonitorMetrics(
        IntPtr monitor, out MonitorInfoRaw info, out double scale)
    {
        info = MonitorInfoW.Create();
        scale = 1;
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        if (GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) != 0 || dpiX == 0)
        {
            dpiX = 96;
        }
        scale = dpiX / 96.0;
        return true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SetDragOver(bool active)
    {
        _root.BorderThickness = active ? new Thickness(2) : new Thickness(1);
        _root.BorderBrush = active
            ? (Brush)FindResourceOrDefault("SystemFillColorAttentionBrush", Brushes.SteelBlue)
            : (Brush)FindResourceOrDefault("CardStrokeColorDefaultBrush", Brushes.LightGray);
    }

    private void OnDropped(object sender, DragEventArgs e)
    {
        SetDragOver(false);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

        foreach (var raw in dropped)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = NormalizePath(raw);
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            bool exists = _paths.Any(existing =>
                string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            if (!exists) _paths.Add(path);
        }
        if (_paths.Count == 0) return;
        AppStorage.Log($"DropPoint accepted {_paths.Count} item(s).");

        AnimateCount();
        _countBadge.Text = _paths.Count.ToString();
        _countBadgeBorder.Visibility = _paths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _iconHost.Visibility = Visibility.Collapsed;
        _fileIcon.Source = ShellIconLoader.Load(_paths[0]);
        _fileIcon.Visibility = _fileIcon.Source is null ? Visibility.Collapsed : Visibility.Visible;
        if (_fileIcon.Source is null) _iconHost.Visibility = Visibility.Visible;
        var firstName = Path.GetFileName(_paths[0]);
        if (string.IsNullOrEmpty(firstName)) firstName = _paths[0];
        _primaryText.Text = _paths.Count == 1 ? firstName : $"{_paths.Count} 个项目";
        _secondaryText.Text = "从窗口拖出以释放到目标";
    }

    private void OnPadMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // With no staged files the whole pad is a drag handle, so moving it
        // does not require hitting the small grip. Once files are staged the
        // same gesture means "drag out", and moving stays on the grip.
        if (_paths.Count == 0)
        {
            if (BeginMove(this)) e.Handled = true;
            return;
        }
        e.Handled = true;

        // CF_HDROP is the format Explorer-style consumers (聊天窗、浏览器上传、
        // 其他 Explorer 窗口) all recognise; SetFileDropList builds it correctly.
        var list = new StringCollection();
        list.AddRange(_paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);

        try
        {
            var result = DragDrop.DoDragDrop(this, data,
                DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move);
            if (result == DragDropEffects.None)
            {
                _secondaryText.Text = "已取消，项目仍在托盘中";
                return;
            }
            CloseSafe();
        }
        catch (Exception ex)
        {
            AppStorage.Log($"DropPoint drag-out failed: {ex.Message}");
            _secondaryText.Text = "拖出失败，请重试";
        }
    }

    private bool BeginMove(FrameworkElement captureTarget)
    {
        if (!GetCursorPos(out _moveStartCursor)) return false;
        _moveStartLeft = Left;
        _moveStartTop = Top;
        _moveScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _isMoving = true;
        captureTarget.CaptureMouse();
        return true;
    }

    private bool UpdateMove(bool leftButtonPressed)
    {
        if (!_isMoving || !leftButtonPressed) return false;
        if (!GetCursorPos(out var cursor)) return false;

        var left = _moveStartLeft + (cursor.X - _moveStartCursor.X) / _moveScale;
        var top = _moveStartTop + (cursor.Y - _moveStartCursor.Y) / _moveScale;
        Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        Top = Math.Clamp(top, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        return true;
    }

    private bool EndMove(FrameworkElement captureTarget)
    {
        if (!_isMoving) return false;
        _isMoving = false;
        captureTarget.ReleaseMouseCapture();
        Moved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void AnimateCount()
    {
        var scale = new ScaleTransform(1, 1);
        _countBadgeBorder.RenderTransform = scale;
        _countBadgeBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        var animation = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void CloseSafe()
    {
        try { Close(); }
        catch (Exception)
        {
            // Close during an in-flight OLE drag can throw if the window is
            // already gone; the process is about to be torn down anyway.
        }
    }

    private static FrameworkElement BuildIconHost()
    {
        var glyph = new TextBlock
        {
            Text = "\uE8B7",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResourceOrDefault("TextFillColorSecondaryBrush", Brushes.Gray),
        };
        return glyph;
    }

    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        AppStorage.Log($"DropPoint hwnd={hwnd}, exstyle=0x{style:X}.");
    }

    private static object FindResourceOrDefault(string key, object fallback)
    {
        try { return Application.Current.TryFindResource(key) ?? fallback; }
        catch (Exception) { return fallback; }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x0800_0000;
    private const int WS_EX_TOOLWINDOW = 0x0080;
    private const uint MonitorDefaultToNearest = 1;
    private const uint MonitorDefaultToPrimary = 2;
    private const int EffectiveDpi = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint ShgfiIcon = 0x0000_0100;
    private const uint ShgfiLargeIcon = 0x0000_0000;

    private static string NormalizePath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // "C:\" must keep its separator, otherwise CF_HDROP consumers resolve
        // "C:" as the process's current directory on that drive.
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    private static class ShellIconLoader
    {
        public static BitmapSource? Load(string path)
        {
            var info = new ShFileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiLargeIcon);
            if (result == IntPtr.Zero || info.HIcon == IntPtr.Zero) return null;
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                    info.HIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                DestroyIcon(info.HIcon);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr HIcon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoRaw
    {
        public int CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public uint DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Helper alias so the struct above reads cleanly at the call site.
    private static class MonitorInfoW
    {
        public static MonitorInfoRaw Create() => new()
        {
            CbSize = Marshal.SizeOf<MonitorInfoRaw>(),
        };
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoRaw info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint fileAttributes, ref ShFileInfo info, uint infoSize, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
