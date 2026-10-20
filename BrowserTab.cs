using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using Microsoft.WindowsAPICodePack.Controls;
using Microsoft.WindowsAPICodePack.Controls.WindowsForms;
using Microsoft.WindowsAPICodePack.Shell;

namespace FileTabs;

/// <summary>
/// One tab owns one ExplorerBrowser instance and its hosting element.
/// All tabs are stacked in the same Grid; activation toggles Visibility only,
/// so each tab keeps its own view state, scroll position and history.
/// </summary>
public sealed class BrowserTab : INotifyPropertyChanged
{
    // CLSID of the shell "This PC" folder, used as the fallback target.
    public const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    public ExplorerBrowser Browser { get; }
    public WindowsFormsHost Host { get; }

    /// <summary>
    /// Raised after navigation or item changes; the handler re-reads
    /// <see cref="Browser"/>.SelectedItems. This fires while the shell view
    /// still holds its selection, so refreshing after the event dispatcher
    /// drains would see an empty collection.
    /// </summary>
    public event EventHandler? SelectionStateChanged;

    /// <summary>
    /// Alt+Left / Alt+Right typed while the native shell view owns the focus.
    /// The argument is -1 for the previous tab and +1 for the next one; the
    /// window turns it into an actual tab switch.
    /// </summary>
    public event Action<int>? TabSwitchRequested;

    // ExplorerBrowser throws when ViewMode is assigned before the shell view
    // exists; remember the request and push it once the handle is created.
    private ExplorerBrowserViewMode _requestedViewMode = ExplorerBrowserViewMode.Auto;
    private int? _requestedThumbnailSize;

    private readonly string _initialPath;
    private bool _initialNavigationDone;

    // ExplorerBrowserNavigationLog keeps its history in private managed
    // fields; the package does not expose a way to remove one synthetic
    // navigation after a pane rebuild.
    private static readonly FieldInfo? NavigationLogLocationsField =
        typeof(ExplorerBrowserNavigationLog).GetField(
            "_locations",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? NavigationLogCurrentIndexField =
        typeof(ExplorerBrowserNavigationLog).GetField(
            "currentLocationIndex",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private string _title = "New tab";
    private bool _isActive;
    private bool _isPinned;
    private string _tabKind = "Folder";
    private ShellObject? _paneRebuildTarget;
    private List<ShellObject>? _historyBeforePaneRebuild;
    private int _historyIndexBeforePaneRebuild;
    private string? _paneRebuildEscapePath;
    private string? _paneRebuildTargetPath;
    private bool _paneRebuildReturnStarted;

    public string Title
    {
        get => _title;
        private set
        {
            _title = value;
            OnPropertyChanged();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Pinned tabs sit at the front of the strip, hide their close button and
    /// ignore close shortcuts; the state travels with the saved session.
    /// </summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            _isPinned = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Folder / Computer / Network / RecycleBin / Library.
    /// The tab template maps this to a glyph; a string avoids depending on
    /// WPF UI's symbol enum in the view model.
    /// </summary>
    public string TabKind
    {
        get => _tabKind;
        private set
        {
            _tabKind = value;
            OnPropertyChanged();
        }
    }

    public BrowserTab(string initialPath)
    {
        _initialPath = initialPath;

        Browser = new ExplorerBrowser { Dock = System.Windows.Forms.DockStyle.Fill };
        // The WPF breadcrumb bar replaces the shell command bar by default.
        // Keep the native navigation pane visible. Shell view state still
        // respects the user's Explorer folder-type settings.
        Browser.NavigationOptions.PaneVisibility.Commands =
            AppSettings.Current.ShowNativeCommandBar
                ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
                : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        Browser.NavigationOptions.PaneVisibility.Navigation =
            AppSettings.Current.ShowNativeSidebar
                ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
                : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        Browser.NavigationOptions.PaneVisibility.Preview = AppSettings.Current.ShowPreviewPane
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        Browser.NavigationOptions.PaneVisibility.Details = AppSettings.Current.ShowDetailsPane
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        Host = new WindowsFormsHost { Child = Browser, Visibility = Visibility.Hidden };

        Browser.NavigationComplete += (_, e) =>
        {
            Title = !string.IsNullOrWhiteSpace(e.NewLocation?.Name)
                ? e.NewLocation!.Name
                : e.NewLocation?.ParsingName ?? string.Empty;
            TabKind = KindFor(e.NewLocation?.ParsingName);
            // Navigation rebuilds the DirectUI view; reapply without it the
            // new shell surface follows the light system theme again.
            ApplyShellTheme();
            // The rebuilt view means new child HWNDs to cover for wheel/keys.
            InstallShellSubclassTree();
            ContinuePaneRebuild(e.NewLocation);
            SelectionStateChanged?.Invoke(this, EventArgs.Empty);
        };
        Browser.HandleCreated += (_, _) =>
        {
            ApplyShellTheme();
            ApplyViewMode();
            InstallShellSubclassTree();
        };
        Browser.HandleDestroyed += (_, _) => ReleaseShellSubclasses();
        Browser.SelectionChanged += (_, _) => SelectionStateChanged?.Invoke(this, EventArgs.Empty);
        Browser.ItemsChanged += (_, _) => SelectionStateChanged?.Invoke(this, EventArgs.Empty);

        // Navigate once the host is loaded and its HWND exists;
        // navigating before that can be dropped by the control.
        Host.Loaded += OnHostLoaded;
    }

    private void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialNavigationDone) return;
        _initialNavigationDone = true;
        ApplyShellTheme();
        try
        {
            Browser.Navigate(ShellObject.FromParsingName(_initialPath));
        }
        catch (Exception)
        {
            // Saved path may no longer exist; fall back instead of failing.
            Browser.Navigate(ShellObject.FromParsingName(ThisPc));
        }
    }

    public void ApplyShellTheme()
    {
        if (Browser.IsHandleCreated)
        {
            ThemeManager.ApplyShellTheme(Browser.Handle);
        }
    }

    private static string KindFor(string? parsingName)
    {
        if (string.IsNullOrEmpty(parsingName)) return "Folder";
        if (parsingName.Equals(ThisPc, StringComparison.OrdinalIgnoreCase)) return "Computer";
        if (parsingName.Contains("F02C1A0D-BE21-4350-88B0-7367FC96EF3C",
                StringComparison.OrdinalIgnoreCase)) return "Network";
        if (parsingName.Contains("645FF040-5081-101B-9F08-00AA002F954E",
                StringComparison.OrdinalIgnoreCase)) return "RecycleBin";
        if (parsingName.Contains("031E4825-7B94-4DC3-B131-E946B44C8DD5",
                StringComparison.OrdinalIgnoreCase)) return "Library";
        return "Folder";
    }

    public void ApplyNativeSidebarVisibility(bool visible)
    {
        Browser.NavigationOptions.PaneVisibility.Navigation = visible
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;

        // The shell only reads pane visibility when it creates the view, so
        // re-navigate the current location to rebuild it with the new state.
        RenavigateCurrentLocation();
    }

    public void ApplyNativeCommandBarVisibility(bool visible)
    {
        Browser.NavigationOptions.PaneVisibility.Commands = visible
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;

        // Like the other pane states, Commands is applied while the shell
        // creates a view, so force a fresh navigation to make it visible
        // before the current tab is re-created.
        RenavigateCurrentLocation();
    }

    /// <summary>
    /// The shell only reads pane visibility when it creates the view, and
    /// Details is not part of the options object once the view exists, so a
    /// re-navigation rebuilds the surface with the new state.
    /// </summary>
    public void ApplyDetailsPaneVisibility(bool visible)
    {
        Browser.NavigationOptions.PaneVisibility.Details = visible
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;

        RenavigateCurrentLocation();
    }

    public void ApplyPreviewPaneVisibility(bool visible)
    {
        Browser.NavigationOptions.PaneVisibility.Preview = visible
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;

        RenavigateCurrentLocation();
    }

    private void RenavigateCurrentLocation()
    {
        if (Browser.NavigationLog.CurrentLocation is not { } location) return;

        // Pane visibility is read while the shell creates its view. Navigating
        // to the same location reuses the existing view even with
        // AlwaysNavigate, so briefly visit another location and come back.
        var escapePath = EscapeLocation(location.ParsingName);
        CaptureHistoryBeforePaneRebuild(location, escapePath);
        Browser.Navigate(ShellObject.FromParsingName(escapePath));
    }

    private void CaptureHistoryBeforePaneRebuild(ShellObject target, string escapePath)
    {
        if (NavigationLogLocationsField?.GetValue(Browser.NavigationLog) is not List<ShellObject> locations)
        {
            return;
        }

        _paneRebuildTarget = target;
        _historyBeforePaneRebuild = new List<ShellObject>(locations);
        _historyIndexBeforePaneRebuild = Browser.NavigationLog.CurrentLocationIndex;
        _paneRebuildEscapePath = escapePath;
        _paneRebuildTargetPath = target.ParsingName;
        _paneRebuildReturnStarted = false;
    }

    private void ContinuePaneRebuild(ShellObject? newLocation)
    {
        if (_paneRebuildTarget is not { } target || _paneRebuildTargetPath is null) return;

        var parsingName = newLocation?.ParsingName;
        if (!_paneRebuildReturnStarted)
        {
            if (!string.Equals(parsingName, _paneRebuildEscapePath, StringComparison.OrdinalIgnoreCase))
            {
                ClearPaneRebuild();
                return;
            }

            // The first navigation must finish before the second one is issued;
            // back-to-back Navigate calls are dropped by the shell control.
            _paneRebuildReturnStarted = true;
            Browser.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!Browser.IsDisposed) Browser.Navigate(target);
                }
                catch (Exception)
                {
                    ClearPaneRebuild();
                }
            }));
            return;
        }

        if (string.Equals(parsingName, _paneRebuildTargetPath, StringComparison.OrdinalIgnoreCase) &&
            NavigationLogLocationsField?.GetValue(Browser.NavigationLog) is List<ShellObject> locations &&
            _historyBeforePaneRebuild is { } originalHistory)
        {
            locations.Clear();
            locations.AddRange(originalHistory);
            NavigationLogCurrentIndexField?.SetValue(
                Browser.NavigationLog,
                _historyIndexBeforePaneRebuild);
        }
        ClearPaneRebuild();
    }

    private void ClearPaneRebuild()
    {
        _paneRebuildTarget = null;
        _historyBeforePaneRebuild = null;
        _paneRebuildEscapePath = null;
        _paneRebuildTargetPath = null;
        _paneRebuildReturnStarted = false;
    }

    private static string EscapeLocation(string current)
    {
        var candidates = new[]
        {
            ThisPc,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        return candidates.FirstOrDefault(candidate =>
                   !string.IsNullOrWhiteSpace(candidate) &&
                   !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
               ?? ThisPc;
    }

    public void SetViewMode(ExplorerBrowserViewMode mode, int? thumbnailSize = null)
    {
        _requestedViewMode = mode;
        _requestedThumbnailSize = thumbnailSize;
        if (!Browser.IsHandleCreated) return;
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        if (!Browser.IsHandleCreated) return;
        // Auto lets each folder use its own saved template; forcing a mode on
        // every navigation would override the user's per-folder view.
        if (_requestedViewMode == ExplorerBrowserViewMode.Auto) return;
        if (_requestedThumbnailSize is { } size)
        {
            Browser.ContentOptions.ThumbnailSize = size;
        }
        Browser.ContentOptions.ViewMode = _requestedViewMode;
    }

    // ---------- native input bridge ----------

    // Ctrl+wheel walks this ladder, the zoom stops of the View menu in order.
    private static readonly (ExplorerBrowserViewMode Mode, int? ThumbnailSize)[] ViewWheelLadder =
    {
        (ExplorerBrowserViewMode.Details, null),
        (ExplorerBrowserViewMode.List, null),
        (ExplorerBrowserViewMode.SmallIcon, null),
        (ExplorerBrowserViewMode.Icon, null),
        (ExplorerBrowserViewMode.Thumbnail, 96),
        (ExplorerBrowserViewMode.Thumbnail, 256),
    };

    private const uint WmMouseWheel = 0x020A;
    private const uint WmSysKeyDown = 0x0104;
    private const int VkControl = 0x11;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;

    // Keyboard and wheel messages go to the deepest shell window, not to the
    // ExplorerBrowser HWND itself, so every descendant needs the subclass.
    // The callback is static, hence the HWND -> tab map; it is cleared when
    // the browser handle is destroyed.
    private static readonly Dictionary<IntPtr, BrowserTab> ShellSubclassOwners = new();
    private readonly HashSet<IntPtr> _shellSubclassed = new();

    private void InstallShellSubclassTree()
    {
        if (!Browser.IsHandleCreated) return;
        InstallShellSubclass(Browser.Handle);
        _ = EnumChildWindows(Browser.Handle, (hwnd, _) =>
        {
            InstallShellSubclass(hwnd);
            return true;
        }, IntPtr.Zero);
    }

    private void InstallShellSubclass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !_shellSubclassed.Add(hwnd)) return;
        ShellSubclassOwners[hwnd] = this;
        SetWindowSubclass(hwnd, ShellSubclassProcRef, UIntPtr.Zero, IntPtr.Zero);
    }

    private void ReleaseShellSubclasses()
    {
        foreach (var hwnd in _shellSubclassed)
        {
            ShellSubclassOwners.Remove(hwnd);
        }
        _shellSubclassed.Clear();
    }

    private static IntPtr ShellSubclassProc(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (!ShellSubclassOwners.TryGetValue(hwnd, out var tab))
        {
            return DefSubclassProc(hwnd, msg, wParam, lParam);
        }

        if (msg == WmMouseWheel && (GetKeyState(VkControl) & 0x8000) != 0)
        {
            var delta = unchecked((short)((long)wParam >> 16));
            tab.StepViewByWheel(delta > 0 ? 1 : -1);
            // Swallow the message so the shell does not scroll as well.
            return IntPtr.Zero;
        }

        // Alt+Left / Alt+Right arrive as system keys; bit 29 of lParam tells
        // apart the Alt-held variant from plain arrow keys.
        if (msg == WmSysKeyDown && (lParam.ToInt64() & (1L << 29)) != 0)
        {
            var vk = wParam.ToInt64();
            if (vk is VkLeft or VkRight)
            {
                tab.TabSwitchRequested?.Invoke(vk == VkLeft ? -1 : 1);
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void StepViewByWheel(int direction)
    {
        if (!Browser.IsHandleCreated || Browser.IsDisposed) return;
        try
        {
            var index = CurrentWheelLadderIndex();
            var next = Math.Clamp(index + direction, 0, ViewWheelLadder.Length - 1);
            if (next == index) return;
            var (mode, size) = ViewWheelLadder[next];
            SetViewMode(mode, size);
        }
        catch (COMException)
        {
            // The shell can be between views mid-navigation; skip this tick.
        }
    }

    private int CurrentWheelLadderIndex()
    {
        ExplorerBrowserViewMode mode;
        var thumbnailSize = 0;
        try
        {
            mode = Browser.ContentOptions.ViewMode;
            thumbnailSize = Browser.ContentOptions.ThumbnailSize;
        }
        catch (COMException)
        {
            return 1;
        }

        return mode switch
        {
            ExplorerBrowserViewMode.Details => 0,
            ExplorerBrowserViewMode.List => 1,
            ExplorerBrowserViewMode.SmallIcon => 2,
            ExplorerBrowserViewMode.Icon => 3,
            ExplorerBrowserViewMode.Thumbnail or ExplorerBrowserViewMode.ThumbStrip =>
                thumbnailSize >= 160 ? 5 : 4,
            // Tile and Content sit outside the ladder; the nearest stop keeps
            // the next tick moving in the expected direction.
            ExplorerBrowserViewMode.Tile => 3,
            ExplorerBrowserViewMode.Content => 1,
            _ => 2,
        };
    }

    private delegate IntPtr SubclassProcDelegate(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData);

    private delegate bool EnumChildProcDelegate(IntPtr hwnd, IntPtr lParam);

    // Keep the delegate alive: SetWindowSubclass stores the raw function
    // pointer for the lifetime of the HWND.
    private static readonly SubclassProcDelegate ShellSubclassProcRef = ShellSubclassProc;

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hwnd, SubclassProcDelegate pfnSubclass, UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parent, EnumChildProcDelegate callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
