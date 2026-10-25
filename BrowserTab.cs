using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
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

    public ExplorerBrowser? Browser { get; private set; }
    public WindowsFormsHost? Host { get; private set; }

    public bool IsMaterialized => Browser is not null;

    public bool HasCompletedNavigation { get; private set; }

    /// <summary>
    /// Location known for this tab before the shell view exists. Saved sessions
    /// restore background tabs from this property instead of creating a view.
    /// </summary>
    public string CurrentPath =>
        Browser?.NavigationLog?.CurrentLocation?.ParsingName ?? _currentPath;

    public ShellObject? CurrentLocation => Browser?.NavigationLog?.CurrentLocation;

    /// <summary>
    /// Path to persist for this tab. When the saved location was unreachable
    /// and the view fell back to This PC, keep the original so a later start
    /// can retry it instead of silently replacing it.
    /// </summary>
    public string SessionPath => _unavailablePath ?? CurrentPath;

    public string SessionTitle => _unavailableTitle ?? Title;

    /// <summary>
    /// Raised after navigation or item changes; the handler re-reads
    /// <see cref="Browser"/>.SelectedItems. This fires while the shell view
    /// still holds its selection, so refreshing after the event dispatcher
    /// drains would see an empty collection.
    /// </summary>
    public event EventHandler? SelectionStateChanged;

    public event EventHandler? NavigationCompleted;

    public event EventHandler? NavigationStateChanged;

    /// <summary>
    /// 该标签第一次真正拿到非零视图项时触发一次。NavigationComplete 只代表
    /// 地址切换完成，此时 DirectUI 列表还没填充；用它收起等待提示会让用户
    /// 只剩系统那句"正在处理…"。这里以 ItemsChanged 且项数 &gt; 0 为准。
    /// 每个标签每个导航周期只触发一次，滚动/刷新不会重复触发。
    /// </summary>
    public event EventHandler? ItemsPresented;

    /// <summary>
    /// Alt+Left / Alt+Right typed while the native shell view owns the focus.
    /// The argument is -1 for the previous tab and +1 for the next one; the
    /// window turns it into an actual tab switch.
    /// </summary>
    public event Action<int>? TabSwitchRequested;

    /// <summary>
    /// A Ctrl shortcut typed while the native shell view owns the focus. The
    /// shell's child HWNDs take the keyboard, so WM_KEYDOWN never reaches the
    /// main window and the WPF key path cannot see it; the subclass reports
    /// the virtual key instead. Arguments are the virtual key code and whether
    /// Shift was held.
    /// </summary>
    public event Action<int, bool>? ShellShortcutRequested;

    // ExplorerBrowser throws when ViewMode is assigned before the shell view
    // exists; remember the request and push it once the handle is created.
    private ExplorerBrowserViewMode _requestedViewMode = ExplorerBrowserViewMode.Auto;
    private int? _requestedThumbnailSize;

    private string _currentPath;
    private bool _initialNavigationDone;
    private int _navigationGeneration;
    private string? _unavailablePath;
    private string? _unavailableTitle;

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
    private bool _isNavigating;
    /// <summary>本轮导航是否已经报过"拿到非零项"，避免滚动/刷新重复触发。</summary>
    private bool _itemsPresented;
    /// <summary>ItemsChanged 脏标志：大目录下事件是风暴（实测 4.8 万次），
    /// 只在 UI 周期合并处理一次，不能逐次执行。</summary>
    private bool _itemsChangedPending;
    private bool _historyNavigationPending;
    private NavigationLogDirection _pendingHistoryDirection;

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

    public BrowserTab(string initialPath, string? initialTitle = null)
    {
        _currentPath = initialPath;
        _title = string.IsNullOrWhiteSpace(initialTitle)
            ? CheapTitleFor(initialPath)
            : initialTitle!;
        _tabKind = KindFor(initialPath);
    }

    /// <summary>
    /// Creates the native shell view. Background restored tabs stay as
    /// metadata until the user first activates them.
    /// </summary>
    public void Materialize()
    {
        if (Browser is not null) return;

        var browser = new ExplorerBrowser { Dock = System.Windows.Forms.DockStyle.Fill };
        Browser = browser;

        // The WPF breadcrumb bar replaces the shell command bar by default.
        // Keep the native navigation pane visible. Shell view state still
        // respects the user's Explorer folder-type settings.
        browser.NavigationOptions.PaneVisibility.Commands =
            AppSettings.Current.ShowNativeCommandBar
                ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
                : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        browser.NavigationOptions.PaneVisibility.Navigation =
            AppSettings.Current.ShowNativeSidebar
                ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
                : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        browser.NavigationOptions.PaneVisibility.Preview = AppSettings.Current.ShowPreviewPane
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        browser.NavigationOptions.PaneVisibility.Details = AppSettings.Current.ShowDetailsPane
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;

        Host = new WindowsFormsHost
        {
            Child = browser,
            Visibility = IsActive ? Visibility.Visible : Visibility.Hidden,
        };

        browser.NavigationComplete += (_, e) =>
        {
            _isNavigating = false;
            _currentPath = e.NewLocation?.ParsingName ?? _currentPath;
            HasCompletedNavigation = true;
            if (_unavailablePath is not null &&
                !string.Equals(_currentPath, ThisPc, StringComparison.OrdinalIgnoreCase))
            {
                // The user navigated away from the fallback; the original
                // location is no longer what this tab represents.
                _unavailablePath = null;
                _unavailableTitle = null;
            }
            var title = !string.IsNullOrWhiteSpace(e.NewLocation?.Name)
                ? e.NewLocation!.Name
                : e.NewLocation?.ParsingName;
            if (!string.IsNullOrWhiteSpace(title)) Title = title!;
            TabKind = KindFor(e.NewLocation?.ParsingName);
            // Navigation rebuilds the DirectUI view; reapply without it the
            // new shell surface follows the light system theme again.
            ApplyShellTheme();
            // The rebuilt view means new child HWNDs to cover for wheel/keys.
            InstallShellSubclassTree();
            ContinuePaneRebuild(e.NewLocation);
            SelectionStateChanged?.Invoke(this, EventArgs.Empty);
            NavigationCompleted?.Invoke(this, EventArgs.Empty);
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            ResumePendingHistoryNavigation();
        };
        browser.NavigationPending += (_, _) =>
        {
            _isNavigating = true;
            // 新一轮导航：重置"已报过项"的标志，否则第二次进目录时不会通知。
            _itemsPresented = false;
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        };
        browser.NavigationFailed += (_, _) =>
        {
            _isNavigating = false;
            ClearPaneRebuild();
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            ResumePendingHistoryNavigation();
        };
        browser.HandleCreated += (_, _) =>
        {
            ApplyShellTheme();
            ApplyViewMode();
            InstallShellSubclassTree();
        };
        browser.HandleDestroyed += (_, _) => ReleaseShellSubclasses();
        browser.SelectionChanged += (_, _) => SelectionStateChanged?.Invoke(this, EventArgs.Empty);
        // ItemsChanged 在大目录下是"事件风暴"：实测 WinSxS(2.4万项) 会在 50ms 内
        // 触发约 4.8 万次。每来一次就刷状态栏 + 同步菜单勾选，会把 UI 线程占满，
        // shell 拿不到消息泵，表现就是"正在加载"永不结束。
        // 所以这里只置一个脏标志，由 UI 周期合并处理，绝不逐次执行。
        browser.ItemsChanged += (_, _) =>
        {
            if (_itemsChangedPending) return;
            _itemsChangedPending = true;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_itemsChangedPending) return;
                _itemsChangedPending = false;
                SelectionStateChanged?.Invoke(this, EventArgs.Empty);
            }));
        };
        // 真正的"枚举完成"信号：大目录下只触发一次（WinSxS 实测 3417ms）。
        // 项数只在这里读一次，避免在事件风暴里实例化整个项集合。
        browser.ViewEnumerationComplete += (_, _) =>
        {
            if (_itemsPresented) return;
            try
            {
                if (browser.Items is { Count: > 0 })
                {
                    _itemsPresented = true;
                    ItemsPresented?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                AppStorage.Log($"ViewEnumerationComplete read failed for {_currentPath}: {ex.GetType().Name}");
            }
        };

        // Navigate once the host is loaded and its HWND exists;
        // navigating before that can be dropped by the control.
        Host.Loaded += (_, _) => StartInitialNavigation();
    }

    public void StartInitialNavigation()
    {
        if (_initialNavigationDone || Browser is not { } browser || !browser.IsHandleCreated) return;
        _initialNavigationDone = true;
        ApplyShellTheme();
        var generation = ++_navigationGeneration;
        var target = _currentPath;
        _ = ResolveAndNavigateAsync(browser, target, generation);
    }

    // Resolving an unreachable UNC path inside SHParseDisplayName blocks for
    // the SMB timeout. Keep it off the dispatcher so the loading overlay and
    // its 3s fallback timer stay responsive.
    private async Task ResolveAndNavigateAsync(
        ExplorerBrowser browser, string target, int generation)
    {
        StartupTiming.Mark($"nav-parse-begin {target}");
        ShellObject? location = null;
        try
        {
            location = await Task.Run(() => ShellObject.FromParsingName(target));
            StartupTiming.Mark($"nav-parse-end {target}");
        }
        catch (Exception)
        {
            StartupTiming.Mark($"nav-parse-failed {target}");
        }

        if (location is null)
        {
            if (generation != _navigationGeneration) return;
            try
            {
                location = await Task.Run(() => ShellObject.FromParsingName(ThisPc));
                _currentPath = ThisPc;
            }
            catch (Exception)
            {
                return;
            }
        }

        if (generation != _navigationGeneration ||
            !ReferenceEquals(Browser, browser) ||
            browser.IsDisposed)
        {
            return;
        }

        try
        {
            _isNavigating = true;
            browser.Navigate(location);
            StartupTiming.Mark("nav-browse-called");
        }
        catch (Exception)
        {
            _isNavigating = false;
            // The view was torn down while the path was being resolved.
        }
    }

    public void MarkUnavailable()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        _unavailablePath = _currentPath;
        _unavailableTitle = Title;
    }

    /// <summary>
    /// Sets the destination before or after materialization. Before the view
    /// exists this only updates metadata; the initial navigation consumes it.
    /// </summary>
    public void NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _currentPath = path;
        Title = CheapTitleFor(path);
        TabKind = KindFor(path);
        if (!IsMaterialized || !_initialNavigationDone) return;
        _navigationGeneration++;
        _isNavigating = true;
        Browser!.Navigate(ShellObject.FromParsingName(path));
    }

    public bool NavigateHistory(NavigationLogDirection direction)
    {
        if (Browser is not { } browser) return false;
        if (!CanNavigateHistory(direction)) return false;
        if (_isNavigating || _historyNavigationPending)
        {
            QueueHistoryNavigation(direction);
            return true;
        }
        if (browser.NavigateLogLocation(direction))
        {
            _isNavigating = true;
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // NavigateLog returns false while another traversal is pending, but
        // CanNavigate* keeps reporting true. Preserve the click and retry it
        // when the current navigation completes or fails.
        QueueHistoryNavigation(direction);
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool CanNavigateHistory(NavigationLogDirection direction)
    {
        var log = Browser?.NavigationLog;
        return direction == NavigationLogDirection.Backward
            ? log?.CanNavigateBackward ?? false
            : log?.CanNavigateForward ?? false;
    }

    private void QueueHistoryNavigation(NavigationLogDirection direction)
    {
        _pendingHistoryDirection = direction;
        _historyNavigationPending = true;
    }

    private void ResumePendingHistoryNavigation()
    {
        if (!_historyNavigationPending || Browser is not { } browser || browser.IsDisposed) return;
        var direction = _pendingHistoryDirection;
        if (!CanNavigateHistory(direction))
        {
            _historyNavigationPending = false;
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (browser.NavigateLogLocation(direction))
        {
            _historyNavigationPending = false;
            _isNavigating = true;
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The shell can still be unwinding. Give it one dispatcher turn before
        // trying again instead of spinning on the UI thread.
        browser.BeginInvoke(new Action(() =>
        {
            if (browser.IsDisposed || !_historyNavigationPending) return;
            ResumePendingHistoryNavigation();
        }));
    }

    public void Dispose()
    {
        _navigationGeneration++;
        _isNavigating = false;
        _historyNavigationPending = false;
        ReleaseShellSubclasses();
        Host?.Dispose();
        Host = null;
        Browser?.Dispose();
        Browser = null;
        _initialNavigationDone = false;
        HasCompletedNavigation = false;
    }

    public void ApplyShellTheme()
    {
        if (Browser is { IsHandleCreated: true } browser)
        {
            ThemeManager.ApplyShellTheme(browser.Handle);
        }
    }

    private static string CheapTitleFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "New tab";
        if (path.Equals(ThisPc, StringComparison.OrdinalIgnoreCase)) return "此电脑";
        if (path.Contains("F02C1A0D-BE21-4350-88B0-7367FC96EF3C",
                StringComparison.OrdinalIgnoreCase)) return "网络";
        if (path.Contains("645FF040-5081-101B-9F08-00AA002F954E",
                StringComparison.OrdinalIgnoreCase)) return "回收站";
        if (path.Contains("031E4825-7B94-4DC3-B131-E946B44C8DD5",
                StringComparison.OrdinalIgnoreCase)) return "库";

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed;
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
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
        if (Browser is not { } browser) return;
        var state = visible
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        if (browser.NavigationOptions.PaneVisibility.Navigation == state) return;
        browser.NavigationOptions.PaneVisibility.Navigation = state;

        // The shell only reads pane visibility when it creates the view, so
        // re-navigate the current location to rebuild it with the new state.
        RenavigateCurrentLocation();
    }

    public void ApplyNativeCommandBarVisibility(bool visible)
    {
        if (Browser is not { } browser) return;
        var state = visible
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        if (browser.NavigationOptions.PaneVisibility.Commands == state) return;
        browser.NavigationOptions.PaneVisibility.Commands = state;

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
        if (Browser is not { } browser) return;
        var state = visible
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        if (browser.NavigationOptions.PaneVisibility.Details == state) return;
        browser.NavigationOptions.PaneVisibility.Details = state;

        RenavigateCurrentLocation();
    }

    public void ApplyPreviewPaneVisibility(bool visible)
    {
        if (Browser is not { } browser) return;
        var state = visible
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        if (browser.NavigationOptions.PaneVisibility.Preview == state) return;
        browser.NavigationOptions.PaneVisibility.Preview = state;

        RenavigateCurrentLocation();
    }

    private void RenavigateCurrentLocation()
    {
        if (Browser is not { } browser ||
            browser.NavigationLog.CurrentLocation is not { } location) return;
        if (_isNavigating) return;

        // Pane visibility is read while the shell creates its view. Navigating
        // to the same location reuses the existing view even with
        // AlwaysNavigate, so briefly visit another location and come back.
        var escapePath = EscapeLocation(location.ParsingName);
        CaptureHistoryBeforePaneRebuild(location, escapePath);
        _isNavigating = true;
        browser.Navigate(ShellObject.FromParsingName(escapePath));
    }

    private void CaptureHistoryBeforePaneRebuild(ShellObject target, string escapePath)
    {
        if (Browser is not { } browser ||
            NavigationLogLocationsField?.GetValue(browser.NavigationLog) is not List<ShellObject> locations)
        {
            return;
        }

        _paneRebuildTarget = target;
        _historyBeforePaneRebuild = new List<ShellObject>(locations);
        _historyIndexBeforePaneRebuild = browser.NavigationLog.CurrentLocationIndex;
        _paneRebuildEscapePath = escapePath;
        _paneRebuildTargetPath = target.ParsingName;
        _paneRebuildReturnStarted = false;
    }

    private void ContinuePaneRebuild(ShellObject? newLocation)
    {
        if (_paneRebuildTarget is not { } target || _paneRebuildTargetPath is null) return;
        if (Browser is not { } browser)
        {
            ClearPaneRebuild();
            return;
        }

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
            browser.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (browser.IsDisposed) return;
                    _isNavigating = true;
                    browser.Navigate(target);
                }
                catch (Exception)
                {
                    _isNavigating = false;
                    ClearPaneRebuild();
                }
            }));
            return;
        }

        if (string.Equals(parsingName, _paneRebuildTargetPath, StringComparison.OrdinalIgnoreCase) &&
            NavigationLogLocationsField?.GetValue(browser.NavigationLog) is List<ShellObject> locations &&
            _historyBeforePaneRebuild is { } originalHistory)
        {
            locations.Clear();
            locations.AddRange(originalHistory);
            NavigationLogCurrentIndexField?.SetValue(
                browser.NavigationLog,
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
        if (Browser is not { IsHandleCreated: true }) return;
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        if (Browser is not { IsHandleCreated: true } browser) return;
        // Auto lets each folder use its own saved template; forcing a mode on
        // every navigation would override the user's per-folder view.
        if (_requestedViewMode == ExplorerBrowserViewMode.Auto) return;
        if (_requestedThumbnailSize is { } size)
        {
            browser.ContentOptions.ThumbnailSize = size;
        }
        browser.ContentOptions.ViewMode = _requestedViewMode;
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
    private const uint WmKeyDown = 0x0100;
    private const int VkControl = 0x11;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkShift = 0x10;
    private const int VkF5 = 0x74;

    // Ctrl shortcuts the window owns. The shell has its own bindings on most
    // of these (Ctrl+F search, Ctrl+L address bar, Ctrl+H history, Ctrl+W
    // close window), so the subclass swallows the key and lets the window act
    // instead. Everything else -- Ctrl+A/C/V/X/Z, Ctrl+Shift+N -- must reach
    // the shell untouched or file operations break.
    private static bool IsWindowShortcut(int vk)
        => vk is 'T' or 'W' or 'H' or 'F' or 'L' or 'S' or 'O' or 'R' or VkF5;

    // Keyboard and wheel messages go to the deepest shell window, not to the
    // ExplorerBrowser HWND itself, so every descendant needs the subclass.
    // The callback is static, hence the HWND -> tab map; it is cleared when
    // the browser handle is destroyed.
    private static readonly Dictionary<IntPtr, BrowserTab> ShellSubclassOwners = new();
    private readonly HashSet<IntPtr> _shellSubclassed = new();

    private void InstallShellSubclassTree()
    {
        if (Browser is not { IsHandleCreated: true } browser) return;
        InstallShellSubclass(browser.Handle);
        _ = EnumChildWindows(browser.Handle, (hwnd, _) =>
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

        // Ctrl shortcuts never reach the main window while the shell owns the
        // keyboard, so the window's PreviewKeyDown handler would never fire.
        // Report the key and swallow it, otherwise the shell runs its own
        // binding for the same chord as well.
        if (msg == WmKeyDown && (GetKeyState(VkControl) & 0x8000) != 0)
        {
            var vk = (int)wParam.ToInt64();
            if (IsWindowShortcut(vk))
            {
                var shift = (GetKeyState(VkShift) & 0x8000) != 0;
                tab.ShellShortcutRequested?.Invoke(vk, shift);
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void StepViewByWheel(int direction)
    {
        if (Browser is not { IsHandleCreated: true } browser || browser.IsDisposed) return;
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
        if (Browser is not { } browser) return 1;
        try
        {
            mode = browser.ContentOptions.ViewMode;
            thumbnailSize = browser.ContentOptions.ThumbnailSize;
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
