using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Diagnostics;
using Microsoft.WindowsAPICodePack.Controls;
using Microsoft.WindowsAPICodePack.Shell;
// WinForms (UseWindowsForms) and WPF both define these names; WPF wins here.
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Wpf.Ui.Controls;

namespace FileTabs;

public partial class MainWindow : FluentWindow
{
    private readonly ExplorerCaptureService _captureService = new();
    private readonly ObservableCollection<BrowserTab> _tabs = new();
    private readonly ObservableCollection<Bookmark> _bookmarks = new();
    private readonly List<RecentLocation> _recentLocations = new();
    private BrowserTab? _active;
    private Bookmark? _renameTarget;
    private BrowserTab? _dragTab;
    private System.Windows.Point _dragStart;
    private bool _isDraggingTab;
    private Bookmark? _dragBookmark;
    private bool _isDraggingBookmark;
    private bool _isSavingSession;
    private bool _isClosing;
    private List<string> _recentlyClosed = new();
    private bool _everythingAvailable = true;
    private DispatcherTimer? _sessionSaveTimer;
    private Rect _normalWindowBounds;
    private List<AddressSuggestion> _addressSuggestions = new();
    private CancellationTokenSource? _addressSuggestionsCts;
    private bool _addressSuggestionChosen;
    private bool _isAddressEditing;
    private bool _isApplyingAppearanceSettings;

    public MainWindow()
    {
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        BookmarkStrip.ItemsSource = _bookmarks;
        _bookmarks.CollectionChanged += (_, _) =>
            BookmarkEmptyHint.Visibility = _bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _tabs.CollectionChanged += (_, _) =>
        {
            UpdateTabCount();
            UpdateTabWidth();
        };
        _bookmarks.CollectionChanged += (_, _) => UpdatePasteButton();
        Activated += (_, _) => UpdatePasteButton();

        Loaded += (_, _) => Startup();
        ToolbarMenu.Opened += (_, _) => SyncToolbarMenuState();
        SizeChanged += (_, _) => UpdateSearchLayout();
        // 用 TitleBar 自己的 SizeChanged：窗口的 SizeChanged 里 ActualWidth
        // 还是上一次布局的值，拖窗口时会一直落后一步，右边按钮会被挤出去。
        MainTitleBar.SizeChanged += (_, _) => UpdateTitleBarLayout();
        TabStrip.SizeChanged += (_, _) => UpdateTabWidth();
        SizeChanged += (_, _) => TrackNormalWindowBounds();
        LocationChanged += (_, _) => TrackNormalWindowBounds();
        StateChanged += (_, _) => TrackNormalWindowBounds();
        Closing += (_, _) => FlushSessionBeforeClose();
        Closing += (_, _) => SaveWindowPlacement();
        Closed += (_, _) => _captureService.Dispose();
        Closed += (_, _) => _isClosing = true;
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => ((HwndSource?)PresentationSource.FromVisual(this))?.AddHook(WndProcHook);

        ProbeEverythingAvailability();
        // Coalesce rapid save triggers (navigation, tab switch, drag, close).
        _sessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _sessionSaveTimer.Tick += (_, _) => SaveCurrentSession();
        _captureService.NewExplorerWindow += OnExplorerWindowCaptured;
        UpdatePasteButton();
        RestoreWindowPlacement();
    }

    private void UpdateTabCount() => TabCountText.Text = _tabs.Count.ToString();

    private void TabListButton_Click(object sender, RoutedEventArgs e)
    {
        TabListMenu.PlacementTarget = TabListButton;
        TabListMenu.IsOpen = true;
    }

    private void TabListMenu_Opened(object sender, RoutedEventArgs e)
    {
        TabListMenu.Items.Clear();
        foreach (var tab in _tabs)
        {
            var item = new MenuItem
            {
                Header = tab.Title,
                IsChecked = tab.IsActive,
                ToolTip = tab.Browser.NavigationLog?.CurrentLocation?.ParsingName,
            };
            item.Click += (_, _) => ActivateTab(tab);
            TabListMenu.Items.Add(item);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // FluentWindow removes the caption (CaptionHeight=0) so Mica can bleed
        // through. Our ui:TitleBar now owns caption hit testing (drag,
        // double-click maximize, system menu) through its own WM_NCHITTEST
        // hook, so the chrome only keeps the resize border.
        base.OnSourceInitialized(e);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            // Must stay 0: the TitleBar decides which parts of its row act as
            // caption, and a WindowChrome caption band on top would swallow
            // clicks on the tabs.
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(4),
            // Must stay 0: -1 (or any non-zero value) extends the DWM glass
            // frame over the whole window, and the hosted shell view then
            // paints its item labels through the glass text path -- white text
            // on the white list background, i.e. an unreadable file list.
            GlassFrameThickness = new Thickness(0),
            CornerRadius = default,
            UseAeroCaptionButtons = false,
        });
    }

    private void ProbeEverythingAvailability()
    {
        Task.Run(() =>
        {
            var ok = EverythingClient.IsAvailable();
            Dispatcher.BeginInvoke(() =>
            {
                _everythingAvailable = ok;
                SearchBox.IsEnabled = ok;
                SearchBox.ToolTip = ok
                    ? "Everything search"
                    : "需要 Everything 正在运行才能搜索";
            });
        });
    }

    private void UpdateSearchLayout()
    {
        var collapsed = ActualWidth <= MinWidth + 20;
        SearchBox.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SearchPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SearchToggleButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    // Sizes of the pieces WPF-UI puts to the right of the TitleBar Header.
    // Keep these in sync with MainWindow.xaml.
    private const double CaptionButtonWidth = 44;   // minimise / maximise / close
    private const double TabListButtonWidth = 30;
    private const double TabListButtonMargin = 6;
    private const double TitleBarTrailingChromeWidth =
        TabListButtonWidth + TabListButtonMargin + 3 * CaptionButtonWidth;

    private const double TabScoopWidth = 8;
    private const double TabStripLeftInset = 8;
    private const double TabMinWidth = 64;
    private const double TabMaxWidth = 240;

    public static readonly DependencyProperty TabWidthProperty = DependencyProperty.Register(
        nameof(TabWidth), typeof(double), typeof(MainWindow),
        new PropertyMetadata(TabMaxWidth));

    /// <summary>统一后的单块标签宽度，绑定在标签模板上。</summary>
    public double TabWidth
    {
        get => (double)GetValue(TabWidthProperty);
        set => SetValue(TabWidthProperty, value);
    }

    private void UpdateTitleBarLayout()
    {
        double headerLeft;
        try
        {
            headerLeft = TabBar.TransformToAncestor(MainTitleBar).Transform(new System.Windows.Point()).X;
        }
        catch (InvalidOperationException)
        {
            return; // 还没挂进可视树
        }

        // Header 在 WPF-UI 模板里不定宽，内容多宽就撑多宽，会把右侧按钮挤出窗口，
        // 所以这里直接定死宽度。
        var available = MainTitleBar.ActualWidth - headerLeft - TitleBarTrailingChromeWidth;
        if (available <= 0) return;
        TabBar.Width = available;
    }

    /// <summary>
    /// 标签统一宽度：按标签区宽度均分，限制在 64~240；左右各留 8px 给
    /// 首尾标签的反向外圆弧，否则圆弧会贴着裁切边界变成直边。
    /// </summary>
    private void UpdateTabWidth()
    {
        var stripWidth = TabStrip.ActualWidth;
        if (stripWidth <= 0) return;
        var count = Math.Max(1, _tabs.Count);
        TabWidth = Math.Clamp(
            (stripWidth - TabStripLeftInset - TabScoopWidth) / count,
            TabMinWidth,
            TabMaxWidth);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        if (msg != WM_KEYDOWN || _isClosing) return IntPtr.Zero;

        const int VK_T = 0x54;
        const int VK_W = 0x57;
        const int VK_H = 0x48;
        var key = (int)wParam;
        var control = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        var shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        if (!control || key != VK_T && key != VK_W && key != VK_H) return IntPtr.Zero;

        if (key == VK_T && shift)
        {
            ReopenClosedTab();
            handled = true;
            return IntPtr.Zero;
        }

        if (key == VK_T)
        {
            AddTab(AppSettings.Current.NewTabPath);
            handled = true;
        }
        else if (key == VK_H)
        {
            ShowHistoryPage();
            handled = true;
        }
        else if (key == VK_W && _active is { } tab)
        {
            CloseTab(tab);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static bool RefreshTab(BrowserTab tab, ShellObject location)
    {
        tab.Browser.Navigate(location);
        return true;
    }

    private void Startup()
    {
        ApplyAppearanceSettings();
        LoadBookmarks();
        LoadRecentLocations();
        if (AppSettings.Current.RestoreLastSession)
        {
            RestoreSession();
        }
        else
        {
            AddTab(AppSettings.Current.NewTabPath);
        }
        ApplyCaptureExplorerSetting();
        UpdateTitleBarLayout();
        UpdateTabWidth();
    }

    private void RestoreWindowPlacement()
    {
        var settings = AppSettings.Current;
        var virtualBounds = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var width = double.IsFinite(settings.WindowWidth) && settings.WindowWidth >= MinWidth
            ? settings.WindowWidth
            : Width;
        var height = double.IsFinite(settings.WindowHeight) && settings.WindowHeight >= MinHeight
            ? settings.WindowHeight
            : Height;
        width = Math.Min(width, virtualBounds.Width);
        height = Math.Min(height, virtualBounds.Height);

        // Guard against monitor changes: an off-screen saved position falls
        // back to the centered default instead of being unreachable.
        double left;
        double top;
        var candidate = new Rect(
            double.IsFinite(settings.WindowLeft)
                ? settings.WindowLeft
                : virtualBounds.Left + ((virtualBounds.Width - width) / 2),
            double.IsFinite(settings.WindowTop)
                ? settings.WindowTop
                : virtualBounds.Top + ((virtualBounds.Height - height) / 2),
            width,
            height);
        if (virtualBounds.IntersectsWith(candidate))
        {
            left = candidate.Left;
            top = candidate.Top;
        }
        else
        {
            left = virtualBounds.Left + ((virtualBounds.Width - width) / 2);
            top = virtualBounds.Top + ((virtualBounds.Height - height) / 2);
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
        _normalWindowBounds = new Rect(left, top, width, height);
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void TrackNormalWindowBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _normalWindowBounds = new Rect(Left, Top, ActualWidth, Height);
    }

    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, Height)
            : _normalWindowBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var settings = AppSettings.Current;
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        AppStorage.SaveSettings(settings);
    }

    private void LoadRecentLocations()
    {
        _recentLocations.Clear();
        _recentLocations.AddRange(AppStorage.LoadRecentLocations()
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .OrderByDescending(item => item.VisitedAt));
        RefreshRecentLocationsDropdown();
    }

    private void ApplyAppearanceSettings()
    {
        BookmarkBar.Visibility = AppSettings.Current.ShowBookmarkBar
            ? Visibility.Visible
            : Visibility.Collapsed;
        // IsChecked raises Checked/Unchecked synchronously. During startup those
        // handlers must not save settings or rebuild shell views; they only
        // mirror the persisted state.
        _isApplyingAppearanceSettings = true;
        try
        {
            ShowBookmarkBarMenuItem.IsChecked = AppSettings.Current.ShowBookmarkBar;
            ShowNativeSidebarMenuItem.IsChecked = AppSettings.Current.ShowNativeSidebar;
            ShowNativeCommandBarMenuItem.IsChecked = AppSettings.Current.ShowNativeCommandBar;
            ShowDetailsPaneMenuItem.IsChecked = AppSettings.Current.ShowDetailsPane;
            ShowPreviewPaneMenuItem.IsChecked = AppSettings.Current.ShowPreviewPane;
        }
        finally
        {
            _isApplyingAppearanceSettings = false;
        }
        DropPointButton.Visibility = AppSettings.Current.DropPointEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!Enum.TryParse<AppTheme>(AppSettings.Current.Theme, out var parsedTheme))
        {
            parsedTheme = AppTheme.FollowSystem;
        }
        ThemeManager.Apply(parsedTheme);
        foreach (var tab in _tabs)
        {
            tab.ApplyShellTheme();
            // Pane visibility is only applied by the checkbox handlers, so a
            // persisted "off" would otherwise leave the pane visible at startup.
            tab.ApplyDetailsPaneVisibility(AppSettings.Current.ShowDetailsPane);
            tab.ApplyPreviewPaneVisibility(AppSettings.Current.ShowPreviewPane);
        }
        _searchSort = AppSettings.Current.SearchSortByName
            ? EverythingClient.SortMode.NameAscending
            : EverythingClient.SortMode.ModifiedDescending;
    }

    // ---------- tabs ----------

    private void AddTab(string path)
    {
        var tab = new BrowserTab(path);
        tab.Browser.NavigationComplete += (_, _) =>
        {
            if (ReferenceEquals(_active, tab)) SyncNavigationBar();
            MarkSessionDirty();
        };
        tab.Browser.NavigationComplete += (_, e) =>
        {
            // Restored background tabs navigate on startup too; only the tab
            // the user is actually looking at belongs in access history.
            if (ReferenceEquals(_active, tab)) RecordRecentLocation(e.NewLocation);
        };
        tab.SelectionStateChanged += (_, _) =>
        {
            if (!ReferenceEquals(_active, tab)) return;
            UpdateStatusBar();
            SyncViewMenuChecks();
        };
        // Alt+Left / Alt+Right typed inside the native shell view; the WPF
        // key path cannot see those because the HwndHost owns the focus.
        tab.TabSwitchRequested += CycleTabBy;
        _tabs.Add(tab);
        ViewContainer.Children.Add(tab.Host);
        // New tabs must inherit the current pane state instead of the shell default.
        tab.ApplyDetailsPaneVisibility(AppSettings.Current.ShowDetailsPane);
        tab.ApplyPreviewPaneVisibility(AppSettings.Current.ShowPreviewPane);
        ActivateTab(tab);
    }

    /// <summary>
    /// Entry point for command-line / pipe handoff: open this path in a new
    /// tab (or activate an existing one), bring window to front. Safe to call
    /// from any thread via Dispatcher.BeginInvoke in the caller.
    /// </summary>
    public void OpenPathInNewTab(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OpenPathInNewTab(path));
            return;
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existing = _tabs.FirstOrDefault(t =>
{
            var parsing = t.Browser.NavigationLog?.CurrentLocation?.ParsingName;
            return parsing is not null && string.Equals(
                parsing.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalized, StringComparison.OrdinalIgnoreCase);
        });
        if (existing is not null)
        {
            ActivateTab(existing);
        }
        else
        {
            AddTab(normalized);
        }
    }

    private void ActivateTab(BrowserTab tab)
    {
        BodyRow.Height = new GridLength(1, GridUnitType.Star);
        HistoryRow.Height = new GridLength(0);
        HistoryPanel.Visibility = Visibility.Collapsed;
        foreach (var t in _tabs)
        {
            t.IsActive = ReferenceEquals(t, tab);
            t.Host.Visibility = t.IsActive ? Visibility.Visible : Visibility.Hidden;
        }
        _active = tab;
        UpdateTabCount();
        SyncNavigationBar();
        UpdateStatusBar();
        MarkSessionDirty();
    }

    /// <summary>
    /// Alt+Left / Alt+Right step through the open tabs and wrap around.
    /// </summary>
    private void CycleTabBy(int direction)
    {
        if (_tabs.Count < 2 || _active is null) return;
        var index = _tabs.IndexOf(_active);
        if (index < 0) return;
        var next = (index + direction + _tabs.Count) % _tabs.Count;
        if (next == index) return;
        ActivateTab(_tabs[next]);
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        if (tab.IsPinned) return;

        var wasActive = ReferenceEquals(_active, tab);
        var closedPath = tab.Browser.NavigationLog.CurrentLocation?.ParsingName;
        _tabs.Remove(tab);
        ViewContainer.Children.Remove(tab.Host);
        tab.Host.Dispose();
        tab.Browser.Dispose();

        if (_tabs.Count == 0)
        {
            if (!string.IsNullOrEmpty(closedPath)) RememberClosedPath(closedPath);
            MarkSessionDirty();
            Close();
            return;
        }
        if (wasActive) ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        if (!string.IsNullOrEmpty(closedPath)) RememberClosedPath(closedPath);
        SaveCurrentSession();
    }

    private void SyncNavigationBar()
    {
        // A background navigation can finish while the address editor is open;
        // do not let that programmatic Text assignment re-open suggestions.
        _isAddressEditing = false;
        var log = _active?.Browser.NavigationLog;
        var location = log?.CurrentLocation;
        var parsing = location?.ParsingName ?? string.Empty;
        // Virtual shell folders (This PC, Network) have no file path; their
        // parsing name is a GUID. Show the friendly name instead, and keep the
        // GUID in Tag so Copy/Edit can still navigate to the real location.
        AddressBox.Text = ShellDisplayName(parsing, location?.Name);
        AddressBox.Tag = parsing;
        UpdateBreadcrumbs(parsing, location?.Name);
        BackButton.IsEnabled = log?.CanNavigateBackward ?? false;
        ForwardButton.IsEnabled = log?.CanNavigateForward ?? false;
        NewFolderButton.IsEnabled = CanModifyCurrentFolder();
        UpdatePasteButton();
    }

    private void UpdateBreadcrumbs(string parsingPath, string? shellName = null)
    {
        BreadcrumbItems.ItemsSource = BuildBreadcrumbs(parsingPath, shellName);
        ShowBreadcrumb(shouldEdit: false);
    }

    private sealed record BreadcrumbSegment(string Path, string Label);

    // Friendly label for a shell location. Drive paths pass through unchanged;
    // virtual folders fall back to the known Chinese names, then to the shell
    // item's own display name.
    private static string ShellDisplayName(string parsingPath, string? shellName)
    {
        if (string.IsNullOrEmpty(parsingPath)) return string.Empty;
        if (parsingPath.Length >= 2 && char.IsLetter(parsingPath[0]) && parsingPath[1] == ':')
            return parsingPath;
        if (ShellFallbackNames.TryGetValue(parsingPath, out var known)) return known;
        return string.IsNullOrWhiteSpace(shellName) ? parsingPath : shellName!;
    }

    // Shell GUIDs whose display name cannot be resolved through ShellObject.Name
    // reliably; show fixed Chinese labels so the breadcrumb never shows a raw GUID.
    private static readonly Dictionary<string, string> ShellFallbackNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"] = "此电脑",
            ["::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"] = "网络",
            ["::{031E4825-7B94-4DC3-B131-E946B44C8DD5}"] = "库",
            ["::{645FF040-5081-101B-9F08-00AA002F954E}"] = "回收站",
        };

    private static List<BreadcrumbSegment> BuildBreadcrumbs(string parsingPath, string? shellName = null)
    {
        var result = new List<BreadcrumbSegment>();
        if (string.IsNullOrEmpty(parsingPath))
        {
            return result;
        }

        // Shell locations like "::{20D04FE0-...}" (This PC) are not drivable
        // paths; resolve their display name once and show them as a single
        // breadcrumb segment.
        // "::{20D04FE0-...}" also has ':' at index 1, so check for a real
        // drive prefix instead of testing that single character.
        var isDrivePath = parsingPath.Length >= 2
                          && char.IsLetter(parsingPath[0])
                          && parsingPath[1] == ':';
        if (!isDrivePath)
        {
            var label = "Shell 位置";
            if (ShellFallbackNames.TryGetValue(parsingPath, out var known))
            {
                label = known;
            }
            else if (!string.IsNullOrWhiteSpace(shellName))
            {
                label = shellName!;
            }
            else
            {
                try
                {
                    var shell = ShellObject.FromParsingName(parsingPath);
                    if (!string.IsNullOrWhiteSpace(shell.Name)) label = shell.Name;
                }
                catch (Exception)
                {
                    // Keep the generic fallback name.
                }
            }
            result.Add(new BreadcrumbSegment(parsingPath, label));
            return result;
        }

        var segments = parsingPath.Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var accumulated = string.Empty;
        foreach (var segment in segments)
        {
            accumulated = accumulated.Length == 0
                ? segment
                : accumulated + Path.DirectorySeparatorChar + segment;
            var label = segment;
            if (accumulated.Length == 2)
            {
                // Root like "C:" -> show as "C:\"
                label = segment + Path.DirectorySeparatorChar;
            }
            result.Add(new BreadcrumbSegment(accumulated, label));
        }
        return result;
    }

    private void ShowBreadcrumb(bool shouldEdit)
    {
        _isAddressEditing = shouldEdit;
        BreadcrumbBar.Visibility = shouldEdit ? Visibility.Collapsed : Visibility.Visible;
        AddressBox.Visibility = shouldEdit ? Visibility.Visible : Visibility.Collapsed;
        if (shouldEdit)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            QueueAddressSuggestions(immediate: true);
        }
        else
        {
            CloseAddressSuggestions();
        }
    }

    private void BreadcrumbBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Single-click anywhere in the breadcrumb's empty area switches to the
        // editable text box; segment clicks stop propagation themselves.
        ShowBreadcrumb(shouldEdit: true);
        e.Handled = true;
    }

    private void BreadcrumbPart_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BreadcrumbSegment seg })
        {
            NavigateActive(seg.Path);
        }
        e.Handled = true;
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        // Prefer the raw parsing name so pasting still resolves virtual folders.
        var path = AddressBox.Tag as string
                   ?? _active?.Browser.NavigationLog.CurrentLocation?.ParsingName;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Clipboard.SetText(path);
            CopyPathButton.ToolTip = $"已复制：{path}";
            // Same confirmation pattern as the toolbar: restore after a beat so
            // hovering later does not keep claiming a copy just happened.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyPathButton.ToolTip = "复制当前路径";
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy failed");
        }
    }

    private void EditAddress_Click(object sender, RoutedEventArgs e) => ShowBreadcrumb(shouldEdit: true);

    private void NavigateActive(string path)
    {
        if (_active is null) return;
        CloseAddressSuggestions();
        try
        {
            _active.Browser.Navigate(ShellObject.FromParsingName(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Navigate failed");
        }
    }

    // ---------- session ----------

    private void RestoreSession()
    {
        var session = AppStorage.LoadSession();
        var paths = session?.Tabs.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
        _recentlyClosed.Clear();
        if (session is { } loadedSession)
        {
            foreach (var p in loadedSession.RecentlyClosed.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                _recentlyClosed.Insert(0, p);
            }
        }

        // First run: take the tab set from 360 Folder if it has one.
        if (paths.Count == 0) paths = Import360.ImportSessionPaths();

        if (paths.Count == 0)
        {
            AddTab(AppSettings.Current.NewTabPath);
            return;
        }
        foreach (var p in paths) AddTab(p);
        // Pinned flags are parallel to the saved paths; the order already has
        // the pinned block at the front, so applying them keeps it intact.
        if (session?.Pinned is { } pinnedFlags)
        {
            for (var i = 0; i < _tabs.Count && i < pinnedFlags.Count; i++)
            {
                _tabs[i].IsPinned = pinnedFlags[i];
            }
        }
        if (session is { } s && s.ActiveIndex >= 0 && s.ActiveIndex < _tabs.Count)
        {
            ActivateTab(_tabs[s.ActiveIndex]);
        }
        else
        {
            ActivateTab(_tabs[0]);
        }
    }

    private SessionData BuildSessionData()
    {
        var paths = new List<string>();
        var pinned = new List<bool>();
        var activeIndex = 0;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var path = _tabs[i].Browser.NavigationLog.CurrentLocation?.ParsingName;
            if (string.IsNullOrEmpty(path)) continue;
            if (ReferenceEquals(_tabs[i], _active)) activeIndex = paths.Count;
            paths.Add(path);
            pinned.Add(_tabs[i].IsPinned);
        }

        return new SessionData
        {
            Tabs = paths,
            ActiveIndex = activeIndex,
            Pinned = pinned,
        };
    }

    private void RememberClosedPath(string path)
    {
        _recentlyClosed.RemoveAll(p =>
            string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentlyClosed.Insert(0, path);
        MarkSessionDirty();
    }

    private void MarkSessionDirty()
    {
        if (_isSavingSession) return;
        _sessionSaveTimer?.Start();
    }

    private void StopSessionSaveTimer() => _sessionSaveTimer?.Stop();

    private void SaveCurrentSession()
    {
        if (_isSavingSession) return;

        _isSavingSession = true;
        StopSessionSaveTimer();
        try
        {
            var data = BuildSessionData();
            data.RecentlyClosed = new List<string>(_recentlyClosed);
            AppStorage.SaveSession(data);
        }
        finally
        {
            _isSavingSession = false;
        }
    }

    private void FlushSessionBeforeClose()
    {
        StopSessionSaveTimer();
        SaveCurrentSession();
    }

    private void ReopenClosedTab()
    {
        if (_recentlyClosed.Count == 0) return;

        var path = _recentlyClosed[0];
        _recentlyClosed.RemoveAt(0);
        AddTab(path);
        MarkSessionDirty();
    }

    // ---------- bookmarks ----------

    private void LoadBookmarks()
    {
        foreach (var b in AppStorage.LoadBookmarks()) _bookmarks.Add(b);

        if (_bookmarks.Count == 0 && Import360.Has360Data)
        {
            var imported = Import360.ImportBookmarks();
            if (imported.Count > 0)
            {
                foreach (var b in imported) _bookmarks.Add(b);
                AppStorage.SaveBookmarks(_bookmarks);
            }
        }
    }

    private void AddBookmarkFromPath(string path)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar);
        if (_bookmarks.Any(b =>
                string.Equals(b.Path.TrimEnd(Path.DirectorySeparatorChar), normalized,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var name = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(name)) name = path;
        _bookmarks.Add(new Bookmark { Name = name, Path = path, IsFolder = Directory.Exists(path) });
        AppStorage.SaveBookmarks(_bookmarks);
    }

    private void BookmarkBar_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void BookmarkBar_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files) AddBookmarkFromPath(f);
        }
    }

    private void Bookmark_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Bookmark bookmark }) return;
        if (e.ChangedButton == MouseButton.Left)
        {
            _dragBookmark = bookmark;
            _dragStart = e.GetPosition(this);
            _isDraggingBookmark = false;
            Mouse.Capture(BookmarkStrip);
        }

        if (e.ChangedButton == MouseButton.Middle || Keyboard.Modifiers == ModifierKeys.Control)
        {
            AddTab(bookmark.Path);
            e.Handled = true;
        }
    }

    private void BookmarkStrip_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragBookmark is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this).X - _dragStart.X;
        if (!_isDraggingBookmark)
        {
            if (Math.Abs(delta) < SystemParameters.MinimumHorizontalDragDistance) return;
            _isDraggingBookmark = true;
        }

        var from = _bookmarks.IndexOf(_dragBookmark);
        if (from < 0) return;
        var bookmarkWidth = 100d;
        if (BookmarkStrip.ItemContainerGenerator.ContainerFromItem(_dragBookmark) is FrameworkElement container)
            bookmarkWidth = Math.Max(30, container.ActualWidth + 2);
        var target = from + (int)Math.Floor(delta / bookmarkWidth);
        target = Math.Max(0, Math.Min(_bookmarks.Count - 1, target));
        if (target == from) return;

        _bookmarks.Move(from, target);
        _dragStart = e.GetPosition(this);
    }

    private void BookmarkStrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragBookmark is null) return;
        var wasDragging = _isDraggingBookmark;
        var bookmark = _dragBookmark;
        _dragBookmark = null;
        _isDraggingBookmark = false;
        BookmarkStrip.ReleaseMouseCapture();
        if (wasDragging)
        {
            AppStorage.SaveBookmarks(_bookmarks);
        }
        else
        {
            NavigateActive(bookmark.Path);
        }
    }

    private static Bookmark? MenuBookmark(object sender)
        => (sender as FrameworkElement)?.DataContext as Bookmark;

    private void BookmarkOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MenuBookmark(sender) is { } b) NavigateActive(b.Path);
    }

    private void BookmarkOpenNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (MenuBookmark(sender) is { } b) AddTab(b.Path);
    }

    private void BookmarkOpenContaining_Click(object sender, RoutedEventArgs e)
    {
        if (MenuBookmark(sender) is not { } b) return;
        var directory = Directory.Exists(b.Path) ? b.Path : Path.GetDirectoryName(b.Path);
        if (!string.IsNullOrEmpty(directory)) NavigateActive(directory);
    }

    private void BookmarkRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuBookmark(sender) is not { } b) return;
        _renameTarget = b;
        RenameBox.Text = b.Name;
        RenamePopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuBookmark(sender) is not { } b) return;
        _bookmarks.Remove(b);
        AppStorage.SaveBookmarks(_bookmarks);
    }

    private void RenameOk_Click(object sender, RoutedEventArgs e) => CommitRename();

    private void RenameCancel_Click(object sender, RoutedEventArgs e)
    {
        RenamePopup.IsOpen = false;
        _renameTarget = null;
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitRename();
        else if (e.Key == Key.Escape)
        {
            RenamePopup.IsOpen = false;
            _renameTarget = null;
        }
    }

    private void CommitRename()
    {
        var name = RenameBox.Text.Trim();
        if (_renameTarget is { } b && name.Length > 0)
        {
            b.Name = name;
            AppStorage.SaveBookmarks(_bookmarks);
            // Force the item template to re-read Name.
            var index = _bookmarks.IndexOf(b);
            _bookmarks[index] = b;
        }
        RenamePopup.IsOpen = false;
        _renameTarget = null;
    }

    // ---------- command handlers ----------

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab(AppSettings.Current.NewTabPath);

    // ---------- file commands ----------

    private string? CurrentFolderPath()
    {
        var path = _active?.Browser.NavigationLog?.CurrentLocation?.ParsingName;
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
    }

    private bool CanModifyCurrentFolder() => CurrentFolderPath() is not null;

    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = CurrentFolderPath();
        if (folder is null) return;
        try
        {
            var name = "新建文件夹";
            var candidate = Path.Combine(folder, name);
            for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            {
                candidate = Path.Combine(folder, $"{name} ({suffix})");
            }
            Directory.CreateDirectory(candidate);
            _active?.Browser.Navigate(ShellObject.FromParsingName(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "无法新建文件夹",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CutButton_Click(object sender, RoutedEventArgs e) => SetFileClipboard(cut: true);

    private void CopyButton_Click(object sender, RoutedEventArgs e) => SetFileClipboard(cut: false);

    private void SetFileClipboard(bool cut)
    {
        if (_active is not { } tab) return;
        var paths = new System.Collections.Specialized.StringCollection();
        foreach (var item in tab.Browser.SelectedItems)
        {
            if (item is not ShellObject shellItem) continue;
            var path = shellItem.ParsingName;
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)) paths.Add(path);
        }
        if (paths.Count == 0) return;

        var data = new System.Windows.DataObject();
        data.SetFileDropList(paths);
        data.SetData("Preferred DropEffect", new MemoryStream(
            BitConverter.GetBytes(cut ? 2 : 5)));
        try
        {
            Clipboard.SetDataObject(data, copy: true);
            UpdatePasteButton();
        }
        catch (ExternalException)
        {
            // Another process owns the clipboard; no partial file operation happened.
        }
    }

    private void UpdatePasteButton()
    {
        PasteButton.IsEnabled = CanModifyCurrentFolder() && ClipboardHasFiles();
    }

    private static bool ClipboardHasFiles()
    {
        try
        {
            return Clipboard.ContainsFileDropList();
        }
        catch (ExternalException)
        {
            // Clipboard is momentarily locked by another process.
            return false;
        }
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = CurrentFolderPath();
        if (folder is null || !ClipboardHasFiles()) return;
        var sources = ClipboardFilePaths();
        if (sources.Count == 0) return;
        RunShellOperation(() => ShellFileOperations.Copy(sources, folder),
            "粘贴失败");
        RefreshActive();
    }

    private static List<string> ClipboardFilePaths()
    {
        var result = new List<string>();
        try
        {
            foreach (var entry in Clipboard.GetFileDropList())
            {
                if (entry is { Length: > 0 } path) result.Add(path);
            }
        }
        catch (ExternalException)
        {
            // Clipboard was taken by another process between the two reads.
        }
        return result;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var sources = SelectedShellPaths();
        if (sources.Count == 0) return;
        RunShellOperation(() => ShellFileOperations.DeleteToRecycleBin(sources),
            "删除失败");
        RefreshActive();
    }

    private List<string> SelectedShellPaths()
    {
        var result = new List<string>();
        if (_active is not { } tab) return result;
        try
        {
            foreach (var item in tab.Browser.SelectedItems)
            {
                if (item is not ShellObject shellItem) continue;
                if (Path.IsPathRooted(shellItem.ParsingName)) result.Add(shellItem.ParsingName);
            }
        }
        catch (COMException)
        {
            // The shell view is between item collections during navigation.
        }
        return result;
    }

    private void RefreshActive()
    {
        if (_active?.Browser.NavigationLog.CurrentLocation is { } location)
        {
            RefreshTab(_active, location);
        }
    }

    private void RunShellOperation(Action operation, string failureTitle)
    {
        try
        {
            operation();
        }
        catch (OperationCanceledException)
        {
            // The user dismissed an overwrite prompt; nothing to report.
        }
        catch (Exception ex) when (ex is COMException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, failureTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PropertiesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not { } tab) return;
        try
        {
            if (tab.Browser.SelectedItems.Count == 1
                && tab.Browser.SelectedItems[0] is ShellObject item)
            {
                ShowShellProperties(item.ParsingName);
            }
            else if (CurrentFolderPath() is { } folder)
            {
                ShowShellProperties(folder);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "无法打开属性",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowShellProperties(string path)
    {
        // SEE_MASK_INVOKEIDLIST is required for shell verbs that need a PIDL,
        // and "properties" is one of them; without it ShellExecuteEx returns
        // success but no dialog appears.
        const int SEE_MASK_INVOKEIDLIST = 0x0000000C;
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SEE_MASK_INVOKEIDLIST,
            Verb = "properties",
            File = path,
            Show = 5,
        };
        if (!ShellExecuteEx(ref info))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public int Mask;
        public IntPtr Hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr HInstApp;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr HkeyClass;
        public int HotKey;
        public IntPtr Icon;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    // ---------- native Explorer takeover ----------

    private void OnExplorerWindowCaptured(int hwnd, string path)
    {
        if (!AppSettings.Current.CaptureExplorer || _isClosing) return;

        // The service already hid the external window; here we only need to
        // add/reuse a tab and close it. Marshalled to the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                OpenPathInNewTab(path);
                ExplorerCaptureService.CloseWindow(hwnd);
            }
            catch (Exception ex)
            {
                AppStorage.Log($"Capture {path} failed: {ex.Message}");
                // Give the window back if the tab could not be opened.
                _ = ShowWindow(new IntPtr(hwnd), 5 /* SW_SHOW */);
            }
        });
    }

    // Win32 helpers for the takeover flow: hide then close the external window.
    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    private void ApplyCaptureExplorerSetting()
    {
        if (AppSettings.Current.CaptureExplorer)
        {
            _captureService.Start();
        }
        else
        {
            _captureService.Stop();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var nativeCommandBar = AppSettings.Current.ShowNativeCommandBar;
        var previewPane = AppSettings.Current.ShowPreviewPane;
        var window = new SettingsWindow { Owner = this };
        window.ShowDialog();
        ApplyCaptureExplorerSetting();
        if (AppSettings.Current.ShowNativeCommandBar != nativeCommandBar)
        {
            foreach (var tab in _tabs)
            {
                tab.ApplyNativeCommandBarVisibility(AppSettings.Current.ShowNativeCommandBar);
            }
        }
        if (AppSettings.Current.ShowPreviewPane != previewPane)
        {
            _isApplyingAppearanceSettings = true;
            try
            {
                ShowPreviewPaneMenuItem.IsChecked = AppSettings.Current.ShowPreviewPane;
            }
            finally
            {
                _isApplyingAppearanceSettings = false;
            }
            foreach (var tab in _tabs)
            {
                tab.ApplyPreviewPaneVisibility(AppSettings.Current.ShowPreviewPane);
            }
        }
        DropPointButton.Visibility = AppSettings.Current.DropPointEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ToolbarMenuButton_Click(object sender, RoutedEventArgs e)
    {
        SyncToolbarMenuState();
        ToolbarMenu.PlacementTarget = ToolbarMenuButton;
        ToolbarMenu.IsOpen = true;
    }

    private void AiButton_Click(object sender, RoutedEventArgs e)
    {
        // 360 在命令区右侧提供 AI 入口；FileTabs 目前没有内置模型服务，
        // 因此明确提示而不是假装打开一个空面板。
        MessageBox.Show(this,
            "AI 入口尚未接入模型服务。\n\n" +
            "接入后这里会用于理解当前目录、批量重命名和整理文件。",
            "AI 助手", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SyncToolbarMenuState()
    {
        ReopenClosedTabMenuItem.IsEnabled = _recentlyClosed.Count > 0;
        OpenTerminalMenuItem.IsEnabled = _active?.Browser.NavigationLog.CurrentLocation is not null;
    }

    private void ReopenClosedTab_Click(object sender, RoutedEventArgs e) => ReopenClosedTab();

    private void ShowBookmarkBar_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isApplyingAppearanceSettings) return;
        var visible = ShowBookmarkBarMenuItem.IsChecked;
        BookmarkBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AppSettings.Current.ShowBookmarkBar = visible;
        AppStorage.SaveSettings(AppSettings.Current);
    }

    private void ShowNativeSidebar_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isApplyingAppearanceSettings) return;
        var visible = ShowNativeSidebarMenuItem.IsChecked;
        AppSettings.Current.ShowNativeSidebar = visible;
        AppStorage.SaveSettings(AppSettings.Current);
        foreach (var tab in _tabs) tab.ApplyNativeSidebarVisibility(visible);
    }

    private void ShowNativeCommandBar_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isApplyingAppearanceSettings) return;
        var visible = ShowNativeCommandBarMenuItem.IsChecked;
        AppSettings.Current.ShowNativeCommandBar = visible;
        AppStorage.SaveSettings(AppSettings.Current);
        foreach (var tab in _tabs) tab.ApplyNativeCommandBarVisibility(visible);
    }

    // ---------- view mode / details pane ----------

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        SyncViewMenuChecks();
        ViewMenu.PlacementTarget = ViewModeButton;
        ViewMenu.IsOpen = true;
    }

    private void ViewMenu_Opened(object sender, RoutedEventArgs e) => SyncViewMenuChecks();

    private void ViewModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || sender is not MenuItem { Tag: string tag }) return;
        var mode = tag switch
        {
            "Details" => ExplorerBrowserViewMode.Details,
            "List" => ExplorerBrowserViewMode.List,
            "SmallIcon" => ExplorerBrowserViewMode.SmallIcon,
            "Icon" => ExplorerBrowserViewMode.Icon,
            "LargeIcon" or "ExtraLargeIcon" => ExplorerBrowserViewMode.Thumbnail,
            "Tile" => ExplorerBrowserViewMode.Tile,
            "Content" => ExplorerBrowserViewMode.Content,
            _ => ExplorerBrowserViewMode.Auto,
        };
        var thumbnailSize = tag switch
        {
            "LargeIcon" => 96,
            "ExtraLargeIcon" => 256,
            _ => (int?)null,
        };
        _active.SetViewMode(mode, thumbnailSize);
        SyncViewMenuChecks();
    }

    // The shell owns the real view state and may cluster two modes under one
    // radio choice, so a best-effort match is all the check marks can show.
    private void SyncViewMenuChecks()
    {
        var current = _active is { } tab && tab.Browser.IsHandleCreated
            ? tab.Browser.ContentOptions.ViewMode
            : ExplorerBrowserViewMode.Auto;
        var thumbnailSize = 0;
        if (_active is { } activeTab && activeTab.Browser.IsHandleCreated)
        {
            try
            {
                thumbnailSize = activeTab.Browser.ContentOptions.ThumbnailSize;
            }
            catch (COMException)
            {
                // The shell can be between views while navigating.
            }
        }
        var isThumbnailView = current is ExplorerBrowserViewMode.Thumbnail
            or ExplorerBrowserViewMode.ThumbStrip;
        foreach (var item in ViewMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not string tag) continue;
            item.IsChecked = tag switch
            {
                "Details" => current == ExplorerBrowserViewMode.Details,
                "List" => current == ExplorerBrowserViewMode.List,
                "SmallIcon" => current == ExplorerBrowserViewMode.SmallIcon,
                "Icon" => current == ExplorerBrowserViewMode.Icon,
                "LargeIcon" => isThumbnailView && thumbnailSize < 160,
                "ExtraLargeIcon" => isThumbnailView && thumbnailSize >= 160,
                "Tile" => current == ExplorerBrowserViewMode.Tile,
                "Content" => current == ExplorerBrowserViewMode.Content,
                _ => false,
            };
        }
    }

    private void ShowDetailsPane_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isApplyingAppearanceSettings) return;
        var visible = ShowDetailsPaneMenuItem.IsChecked;
        AppSettings.Current.ShowDetailsPane = visible;
        AppStorage.SaveSettings(AppSettings.Current);
        foreach (var tab in _tabs) tab.ApplyDetailsPaneVisibility(visible);
        UpdateStatusBar();
    }

    private void ShowPreviewPane_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isApplyingAppearanceSettings) return;
        var visible = ShowPreviewPaneMenuItem.IsChecked;
        AppSettings.Current.ShowPreviewPane = visible;
        AppStorage.SaveSettings(AppSettings.Current);
        foreach (var tab in _tabs) tab.ApplyPreviewPaneVisibility(visible);
    }

    // ---------- status bar ----------

    private void UpdateStatusBar()
    {
        if (_active is not { } tab)
        {
            StatusBar.Visibility = Visibility.Collapsed;
            StatusText.Text = string.Empty;
            StatusHintText.Text = string.Empty;
            return;
        }
        try
        {
            var items = tab.Browser.Items;
            var selected = tab.Browser.SelectedItems;
            var selectedCount = selected.Count;
            var fileBytes = 0UL;
            var folders = 0;
            for (var i = 0; i < selectedCount; i++)
            {
                if (selected[i] is not { } item) continue;
                if (item is ShellFolder) { folders++; continue; }
                // Virtual shell items have no Size property; they just do not
                // contribute to the byte total.
                if (item.Properties.System.Size.Value is not { } size) continue;
                fileBytes += size;
            }
            var totalCount = items.Count;
            // The shell status bar already covers the common cases: total item
            // count, item size and "N selected". Keep FileTabs' bar only when
            // the selection is entirely folders; that child count is the one
            // aggregate the native view does not show.
            if (selectedCount == 0 || selectedCount != folders)
            {
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = string.Empty;
                StatusHintText.Text = string.Empty;
            }
            else
            {
                StatusBar.Visibility = Visibility.Visible;
                StatusText.Text = $"已选择 {selectedCount} 个项目" +
                    (fileBytes > 0 ? $" · {FormatByteSize(fileBytes)}" : string.Empty);
                StatusHintText.Text = folders > 0
                    ? $"{totalCount} 个项目 · 含 {folders} 个文件夹"
                    : $"{totalCount} 个项目";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // The shell view is mid-navigation and its item collections are
            // not readable yet; the next navigation event refreshes the bar.
            StatusBar.Visibility = Visibility.Collapsed;
            StatusText.Text = string.Empty;
            StatusHintText.Text = string.Empty;
        }
    }

    private static string FormatByteSize(ulong size)
    {
        if (size < 1024) return $"{size} B";
        if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
        if (size < 1024UL * 1024 * 1024) return $"{size / 1024.0 / 1024:F1} MB";
        return $"{size / 1024.0 / 1024 / 1024:F2} GB";
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        var path = _active?.Browser.NavigationLog.CurrentLocation?.ParsingName;
        if (string.IsNullOrWhiteSpace(path)) return;

        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return;

        var terminal = Enum.TryParse<TerminalKind>(AppSettings.Current.Terminal, out var parsed)
            ? parsed
            : TerminalKind.WindowsTerminal;
        switch (terminal)
        {
            case TerminalKind.PowerShell:
                StartProcess("powershell.exe", "-NoExit", directory);
                break;
            case TerminalKind.CommandPrompt:
                StartProcess("cmd.exe", "/K cd /d \"" + directory + "\"", directory);
                break;
            case TerminalKind.Warp:
                if (!TryStartWarp(directory))
                {
                    StartWindowsTerminalOrCommandPrompt(directory);
                }
                break;
            default:
                StartWindowsTerminalOrCommandPrompt(directory);
                break;
        }
    }

    private static bool TryStartWarp(string directory)
    {
        try
        {
            var warp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Warp", "warp.exe");
            var target = "Warp://action/new_tab?path=" + Uri.EscapeDataString(directory);
            if (File.Exists(warp))
            {
                Process.Start(new ProcessStartInfo(warp, $"\"{target}\"")
                {
                    UseShellExecute = true,
                    WorkingDirectory = directory,
                });
                return true;
            }
            StartProcess("warp.exe", $"\"{target}\"", directory);
            return true;
        }
        catch (Exception)
        {
            // Warp is optional; let the caller fall back like Windows Terminal.
            return false;
        }
    }

    private static void StartWindowsTerminalOrCommandPrompt(string directory)
    {
        try
        {
            StartProcess("wt.exe", $"-d \"{directory}\"", directory);
        }
        catch (Exception)
        {
            // Windows Terminal is optional; fall back to the system console.
            StartProcess("cmd.exe", "/K cd /d \"" + directory + "\"", directory);
        }
    }

    private static void StartProcess(string fileName, string arguments, string workingDirectory)
    {
        Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        });
    }

    private void DropPointButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.Current.DropPointEnabled)
        {
            MessageBox.Show(this, "拖放中转已在设置中关闭。", "FileTabs",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        (System.Windows.Application.Current as App)?.DropPoints.ShowPadForMainWindow();
    }

    // ---------- workspaces ----------

    private void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var popup = new Window
        {
            Title = "保存工作区",
            Owner = this,
            Width = 320,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
        };
        var input = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(12),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var ok = new Button { Content = "保存", Width = 72, Height = 28, IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 72, Height = 28, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                MessageBox.Show(popup, "请输入工作区名称。", "FileTabs",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            popup.DialogResult = true;
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Children = { ok, cancel },
        };
        popup.Content = new StackPanel { Children = { input, buttons } };
        input.Focus();

        if (popup.ShowDialog() != true) return;
        var name = input.Text.Trim();

        var session = BuildSessionData();
        if (session.Tabs.Count == 0)
        {
            MessageBox.Show(this, "当前没有可保存的标签页。", "FileTabs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var workspaces = AppStorage.LoadWorkspaces();
        var existing = workspaces.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal));
        if (existing != null)
        {
            var overwrite = MessageBox.Show(this, $"工作区「{name}」已存在，覆盖？", "FileTabs",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (overwrite != MessageBoxResult.Yes) return;
            existing.Tabs = session.Tabs;
            existing.ActiveIndex = session.ActiveIndex;
            existing.LastUsedAt = DateTime.Now;
        }
        else
        {
            workspaces.Add(new Workspace
            {
                Name = name,
                Tabs = session.Tabs,
                ActiveIndex = session.ActiveIndex,
                LastUsedAt = DateTime.Now,
            });
        }

        AppStorage.SaveWorkspaces(workspaces);
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WorkspaceWindow(this);
        if (dialog.ShowDialog() != true) return;

        var match = AppStorage.LoadWorkspaces()
            .FirstOrDefault(w => string.Equals(w.Name, dialog.SelectedWorkspaceName, StringComparison.Ordinal));
        if (match is null)
        {
            MessageBox.Show(this, "工作区不存在，可能已被删除。", "FileTabs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Close all current tabs manually instead of via CloseTab: the last
        // CloseTab call would close the window, and we must not push closed
        // paths into "recently closed" during a workspace switch.
        _recentlyClosed.Clear();
        foreach (var tab in _tabs.Reverse())
        {
            _tabs.Remove(tab);
            ViewContainer.Children.Remove(tab.Host);
            tab.Host.Dispose();
            tab.Browser.Dispose();
        }

        foreach (var path in match.Tabs.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            AddTab(path);
        }
        if (_tabs.Count == 0)
        {
            AddTab(AppSettings.Current.NewTabPath);
        }
        var active = Math.Min(Math.Max(match.ActiveIndex, 0), _tabs.Count - 1);
        ActivateTab(_tabs[active]);
        match.LastUsedAt = DateTime.Now;
        var workspaces = AppStorage.LoadWorkspaces();
        var stored = workspaces.FirstOrDefault(w => string.Equals(w.Name, match.Name, StringComparison.Ordinal));
        if (stored != null) stored.LastUsedAt = DateTime.Now;
        AppStorage.SaveWorkspaces(workspaces);
    }

    private void TabStripBackground_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            AddTab(AppSettings.Current.NewTabPath);
            e.Handled = true;
        }
    }

    // ui:TitleBar shows the window system menu on every right click bubbling up
    // to it, which swallows the per-tab context menu declared in the tab
    // template. Stop the event at the tab strip so the menu wins.
    private void TabStrip_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void TabStrip_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed) return;
        // Pinned tabs keep their slot, so they are not draggable.
        if (_dragTab.IsPinned) return;
        var delta = e.GetPosition(this).X - _dragStart.X;
        if (!_isDraggingTab)
        {
            if (Math.Abs(delta) < SystemParameters.MinimumHorizontalDragDistance) return;
            _isDraggingTab = true;
        }

        var from = _tabs.IndexOf(_dragTab);
        if (from < 0) return;
        var tabWidth = 164d;
        if (TabStrip.ItemContainerGenerator.ContainerFromItem(_dragTab) is FrameworkElement container)
            tabWidth = Math.Max(40, container.ActualWidth - 12);
        var target = from + (int)Math.Floor(delta / tabWidth);
        // Never drag an unpinned tab into the pinned block.
        target = Math.Max(_tabs.Count(t => t.IsPinned), Math.Min(_tabs.Count - 1, target));
        if (target == from) return;

        _tabs.Move(from, target);
        _dragStart = e.GetPosition(this);
    }

    private void TabStrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragTab is null) return;
        var tab = _dragTab;
        _dragTab = null;
        _isDraggingTab = false;
        MarkSessionDirty();
    }

    private void TabItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab }) return;
        if (e.ChangedButton == MouseButton.Left)
        {
            ActivateTab(tab);
            _dragTab = tab;
            _dragStart = e.GetPosition(this);
            _isDraggingTab = false;
        }
        if (e.ChangedButton == MouseButton.Middle)
        {
            CloseTab(tab);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            ActivateTab(tab);
        }
    }

    // The tab strip lives inside ui:TitleBar, whose own right-click handling
    // wins over the implicit ContextMenu channel, so open the menu explicitly.
    private void TabItem_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element) return;
        menu.PlacementTarget = element;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab }) CloseTab(tab);
    }

    // Pinned tabs form a block at the front: pinning appends to that block,
    // unpinning drops the tab right after it.
    private void TabPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab }) return;
        var from = _tabs.IndexOf(tab);
        if (from < 0) return;

        var pinnedCount = _tabs.Count(t => t.IsPinned);
        var target = tab.IsPinned ? pinnedCount - 1 : pinnedCount;
        if (target != from) _tabs.Move(from, target);
        MarkSessionDirty();
    }

    private void TabCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab }) return;
        foreach (var other in _tabs.Where(t => !ReferenceEquals(t, tab)).ToList())
        {
            CloseTab(other);
        }
    }

    private void TabCloseRight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab }) return;
        var index = _tabs.IndexOf(tab);
        for (var i = _tabs.Count - 1; i > index; i--)
        {
            CloseTab(_tabs[i]);
        }
    }

    private void TabDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab }) return;
        var path = tab.Browser.NavigationLog.CurrentLocation?.ParsingName
                   ?? AppSettings.Current.NewTabPath;
        AddTab(path);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _active?.Browser.NavigateLogLocation(NavigationLogDirection.Backward);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        _active?.Browser.NavigateLogLocation(NavigationLogDirection.Forward);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.Browser.NavigationLog.CurrentLocation?.Parent is { } parent)
        {
            NavigateActive(parent.ParsingName);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.Browser.NavigationLog.CurrentLocation is { } location)
        {
            RefreshTab(_active, location);
        }
    }

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
    }

    private void Go_Click(object sender, RoutedEventArgs e) => NavigateAddressBox();

    // The box shows friendly names ("此电脑"), so map them back to the shell
    // GUID before navigating; real paths pass through untouched.
    private void NavigateAddressBox()
    {
        var typed = AddressBox.Text.Trim();
        if (typed.Length == 0) return;
        var target = ShellFallbackNames.FirstOrDefault(
            pair => string.Equals(pair.Value, typed, StringComparison.OrdinalIgnoreCase)).Key ?? typed;
        NavigateActive(target);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (TryAcceptAddressSuggestion())
            {
                e.Handled = true;
                return;
            }
            NavigateAddressBox();
            ShowBreadcrumb(shouldEdit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseAddressSuggestions();
            ShowBreadcrumb(shouldEdit: false);
            e.Handled = true;
        }
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // TextBox handles arrow keys before KeyDown reaches instance handlers.
        if (!AddressSuggestPopup.IsOpen) return;
        if (e.Key == Key.Down)
        {
            MoveAddressSuggestionSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveAddressSuggestionSelection(-1);
            e.Handled = true;
        }
    }

    private void AddressBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        // StaysOpen is true so arrow keys do not dismiss the popup before
        // KeyDown runs. Close it explicitly unless focus moved into the list.
        if (e.NewFocus is DependencyObject focused
            && AddressSuggestPopup.Child is UIElement popupContent
            && (ReferenceEquals(focused, popupContent) || popupContent.IsAncestorOf(focused)))
        {
            return;
        }
        CloseAddressSuggestions();
    }

    private void AddressBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isAddressEditing) return;
        // Editing invalidates a keyboard-selected item immediately; the
        // replacement list may not have arrived yet.
        _addressSuggestionChosen = false;
        AddressSuggestList.SelectedIndex = -1;
        QueueAddressSuggestions();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+Left / Alt+Right hop between tabs. With focus on a WPF control
        // the key lands here; inside the shell view the BrowserTab subclass
        // hook reports it through TabSwitchRequested instead.
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Left or Key.Right)
            {
                CycleTabBy(key == Key.Left ? -1 : 1);
                e.Handled = true;
                return;
            }
        }

        var shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        if (e.Key == Key.T && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && shift)
        {
            ReopenClosedTab();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.F5 || e.Key == Key.R)
        {
            if (_active?.Browser.NavigationLog.CurrentLocation is { } location)
            {
                RefreshTab(_active, location);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            ShowBreadcrumb(shouldEdit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.S)
        {
            SaveWorkspace_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.O)
        {
            OpenWorkspace_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.T)
        {
            AddTab(AppSettings.Current.NewTabPath);
            e.Handled = true;
        }
        else if (e.Key == Key.W && _active is { } tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
        else if (e.Key == Key.H)
        {
            ShowHistoryPage();
            e.Handled = true;
        }
    }

    // Chrome-style access history: navigation events are coalesced so fast
    // programmatic navigation and duplicate refreshes do not flood the list.
    private void RecordRecentLocation(ShellObject? location)
    {
        var path = location?.ParsingName;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (_recentLocations.Count > 0 &&
            string.Equals(_recentLocations[0].Path, path, StringComparison.OrdinalIgnoreCase) &&
            DateTime.Now - _recentLocations[0].VisitedAt < TimeSpan.FromSeconds(2))
        {
            _recentLocations[0] = new RecentLocation { Path = path, VisitedAt = DateTime.Now };
            return;
        }

        _recentLocations.RemoveAll(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        _recentLocations.Insert(0, new RecentLocation { Path = path, VisitedAt = DateTime.Now });
        if (_recentLocations.Count > 500) _recentLocations.RemoveRange(500, _recentLocations.Count - 500);
        AppStorage.SaveRecentLocations(_recentLocations);
        RefreshRecentLocationsDropdown();
        if (HistoryPanel.Visibility == Visibility.Visible) RefreshHistoryPage();
    }

    // ---------- Everything search ----------

    private CancellationTokenSource? _searchCts;

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var query = SearchBox.Text.Trim();

        if (query.Length == 0)
        {
            SearchResults.ItemsSource = null;
            CloseSearchPanel();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, token);
                var sort = _searchSort;
                // Two-second ceiling for the display update; the native query
                // may keep running underneath but we no longer block the panel.
                var searchTask = Task.Run(() => EverythingClient.Search(query, 100, sort: sort), token);
                var completed = await Task.WhenAny(searchTask, Task.Delay(2000, token));
                var results = completed == searchTask ? await searchTask : null;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (results is null)
                    {
                        SearchResultCount.Text = "0 个结果";
                        SearchHintText.Text = "Everything 搜索超时";
                        SearchHintPopup.IsOpen = true;
                        SearchResults.ItemsSource = null;
                        OpenSearchPanel();
                        return;
                    }
                    SearchResults.ItemsSource = results;
                    SearchResultCount.Text = $"{results.Count} 个结果";
                    OpenSearchPanel();
                    if (SearchResults.Items.Count > 0) SearchResults.SelectedIndex = 0;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    SearchHintText.Text = ex.Message; SearchHintPopup.IsOpen = true;
                    SearchResults.ItemsSource = null;
                    OpenSearchPanel();
                });
            }
        }, token);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when SearchResultsPanel.Visibility == Visibility.Visible && SearchResults.Items.Count > 0:
                SearchResults.Focus();
                SearchResults.SelectedIndex = Math.Max(0, SearchResults.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelectedSearchResult();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearchPanel();
                e.Handled = true;
                break;
        }
    }

    private void SearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up when SearchResults.SelectedIndex <= 0:
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelectedSearchResult();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearchPanel();
                e.Handled = true;
                break;
        }
    }

    private void SearchResults_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        OpenSelectedSearchResult();
        e.Handled = true;
    }

    private void OpenSelectedSearchResult()
    {
        if (SearchResults.SelectedItem is not EverythingSearchResult result) return;
        try
        {
            if (result.IsFolder)
            {
                OpenFolderTab(result.FullPath);
            }
            else
            {
                Process.Start(new ProcessStartInfo(result.FullPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open search result failed");
        }
    }

    // Chrome-style access-history window: matches the recent-items dropdown,
    // but exposes the whole local list with a simple path filter.
    private void RefreshRecentLocationsDropdown()
    {
        RecentLocationsList.ItemsSource = _recentLocations
            .Take(10)
            .Select(item => new RecentLocationDisplay(item))
            .ToList();
    }

    private void RecentLocationsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRecentLocationsDropdown();
        RecentLocationsPopup.IsOpen = true;
    }

    private void OpenSelectedRecentLocation()
    {
        if (RecentLocationsList.SelectedItem is not RecentLocationDisplay item) return;
        RecentLocationsPopup.IsOpen = false;
        OpenFolderTab(item.Path);
    }

    private void RecentLocationsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        OpenSelectedRecentLocation();
    }

    private void RecentLocationsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                OpenSelectedRecentLocation();
                e.Handled = true;
                break;
            case Key.Escape:
                RecentLocationsPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void RecentLocationsOpenFullPage_Click(object sender, RoutedEventArgs e)
    {
        RecentLocationsPopup.IsOpen = false;
        ShowHistoryPage();
    }

    private void ShowHistoryPage()
    {
        RecentLocationsPopup.IsOpen = false;
        // Keep the file view mounted; the drawer takes the bottom 45%.
        HistoryRow.Height = new GridLength(45, GridUnitType.Star);
        BodyRow.Height = new GridLength(55, GridUnitType.Star);
        HistoryPanel.Visibility = Visibility.Visible;
        RefreshHistoryPage();
        HistorySearchBox.Focus();
    }

    private void CloseHistoryPage()
    {
        BodyRow.Height = new GridLength(1, GridUnitType.Star);
        HistoryRow.Height = new GridLength(0);
        HistoryPanel.Visibility = Visibility.Collapsed;
        HistorySearchBox.Clear();
        HistoryItems.ItemsSource = null;
        if (_active is null && _tabs.Count > 0) ActivateTab(_tabs[0]);
    }

    private void HistoryPageClose_Click(object sender, RoutedEventArgs e) => CloseHistoryPage();

    private static string RecentLocationTimeText(DateTime visitedAt)
    {
        var today = DateTime.Today;
        if (visitedAt.Date == today) return visitedAt.ToString("HH:mm");
        if (visitedAt.Date == today.AddDays(-1)) return $"昨天 {visitedAt:HH:mm}";
        return visitedAt.ToString("yyyy-MM-dd HH:mm");
    }

    private void RefreshHistoryPage()
    {
        var query = HistorySearchBox.Text.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _recentLocations.ToList()
            : _recentLocations.Where(item =>
                item.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        HistoryItems.ItemsSource = matches.Select(item => new RecentLocationDisplay(item)).ToList();
        HistoryEmptyHint.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistorySearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshHistoryPage();
    }

    private void HistorySearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when HistoryItems.Items.Count > 0:
                HistoryItems.Focus();
                HistoryItems.SelectedIndex = 0;
                e.Handled = true;
                break;
            case Key.Enter when HistoryItems.Items.Count > 0:
                HistoryItems.SelectedIndex = 0;
                OpenSelectedHistoryItem();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseHistoryPage();
                e.Handled = true;
                break;
        }
    }

    private void OpenSelectedHistoryItem()
    {
        if (HistoryItems.SelectedItem is not RecentLocationDisplay item) return;
        CloseHistoryPage();
        OpenFolderTab(item.Path);
    }

    private void HistoryItems_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        OpenSelectedHistoryItem();
    }

    private void HistoryItems_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                OpenSelectedHistoryItem();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseHistoryPage();
                e.Handled = true;
                break;
        }
    }

    private sealed record RecentLocationDisplay
    {
        public RecentLocationDisplay(RecentLocation location)
        {
            Path = location.Path;
            VisitedAt = location.VisitedAt;
        }

        public string Path { get; }
        public DateTime VisitedAt { get; }
        public string DisplayTitle => ShellDisplayName(Path, TryShellName(Path));
        public string VisitedAtText => RecentLocationTimeText(VisitedAt);

        private static string? TryShellName(string path)
        {
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return null;
            try
            {
                return ShellObject.FromParsingName(path).Name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private EverythingClient.SortMode _searchSort = EverythingClient.SortMode.ModifiedDescending;

    private void OpenSearchPanel()
    {
        SearchResultsPanel.Visibility = Visibility.Visible;
        SearchPanelSplitter.Visibility = Visibility.Visible;
        // The panel lives in its own column; giving the panel a Width while the
        // column stays 0 leaves it clipped to nothing, so size the column.
        SearchResultsColumn.Width = new GridLength(300);
        // Deliberately keep focus in the search box: moving it to the list here
        // would swallow the next keystrokes while the user is still typing.
        // The Down key already hands focus to the list on demand.
    }

    private void CloseSearchPanel()
    {
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        SearchPanelSplitter.Visibility = Visibility.Collapsed;
        SearchResultsColumn.Width = new GridLength(0);
        SearchResults.ItemsSource = null;
    }

    private void SearchPanelClose_Click(object sender, RoutedEventArgs e) => CloseSearchPanel();

    private void SearchResultOpen_Click(object sender, RoutedEventArgs e) => OpenSelectedSearchResult();

    private void SearchResultOpenContaining_Click(object sender, RoutedEventArgs e)
    {
        if (SearchResults.SelectedItem is not EverythingSearchResult result) return;
        OpenFolderTab(result.IsFolder ? result.FullPath : result.Path);
    }

    private void SearchResultCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SearchResults.SelectedItem is not EverythingSearchResult result) return;
        try
        {
            Clipboard.SetText(result.FullPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "复制路径失败");
        }
    }

    private void OpenEverything_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        try
        {
            Process.Start(new ProcessStartInfo(
                $"everything://{Uri.EscapeDataString(query)}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开 Everything 失败");
        }
    }

    // Open a folder as a tab, reusing an existing tab that already shows it.
    private void OpenFolderTab(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var normalized = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existing = _tabs.FirstOrDefault(t =>
        {
            var parsing = t.Browser.NavigationLog.CurrentLocation?.ParsingName;
            if (string.IsNullOrEmpty(parsing)) return false;
            parsing = parsing.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(parsing, normalized, StringComparison.OrdinalIgnoreCase);
        });
        if (existing is { })
        {
            ActivateTab(existing);
            return;
        }
        AddTab(folderPath);
    }

    // ---------- address bar autocomplete ----------

    private sealed record AddressSuggestion(string Name, string Path, bool IsFolder);

    private const int MaxAddressSuggestions = 14;

    private void QueueAddressSuggestions(bool immediate = false)
    {
        _addressSuggestionsCts?.Cancel();

        var cts = new CancellationTokenSource();
        _addressSuggestionsCts = cts;
        var query = AddressBox.Text;
        var recentPaths = _recentLocations.Select(item => item.Path).ToList();
        var currentPath = _active?.Browser.NavigationLog.CurrentLocation?.ParsingName;
        _ = RefreshAddressSuggestionsAsync(query, recentPaths, currentPath, cts, immediate);
    }

    private async Task RefreshAddressSuggestionsAsync(
        string query,
        IReadOnlyList<string> recentPaths,
        string? currentPath,
        CancellationTokenSource cts,
        bool immediate)
    {
        var token = cts.Token;
        try
        {
            // A short debounce keeps typing responsive while still making the
            // list update feel immediate. Directory enumeration runs off the UI
            // thread because network folders can block for seconds.
            if (!immediate) await Task.Delay(120, token);
            var suggestions = await Task.Run(
                () => BuildAddressSuggestions(query, recentPaths, currentPath, token),
                token);
            if (token.IsCancellationRequested
                || !_isAddressEditing
                || !string.Equals(AddressBox.Text, query, StringComparison.Ordinal))
            {
                return;
            }
            ApplyAddressSuggestions(suggestions);
        }
        catch (OperationCanceledException)
        {
            // Superseded by the next keystroke or by closing the editor.
        }
        finally
        {
            if (ReferenceEquals(_addressSuggestionsCts, cts))
            {
                _addressSuggestionsCts = null;
            }
            cts.Dispose();
        }
    }

    private static List<AddressSuggestion> BuildAddressSuggestions(
        string rawQuery,
        IReadOnlyList<string> recentPaths,
        string? currentPath,
        CancellationToken token)
    {
        var results = new List<AddressSuggestion>();
        var query = rawQuery.Trim();
        var expandedQuery = Environment.ExpandEnvironmentVariables(query);

        token.ThrowIfCancellationRequested();
        if (expandedQuery.Length > 0
            && Path.IsPathFullyQualified(expandedQuery))
        {
            AddSuggestion(results, expandedQuery);
        }

        AddDirectoryMatches(results, expandedQuery, token);

        foreach (var recent in recentPaths)
        {
            if (results.Count >= MaxAddressSuggestions) break;
            if (!IsAddressPathMatch(recent, expandedQuery)) continue;
            AddSuggestion(results, recent);
        }

        var current = currentPath;
        while (!string.IsNullOrEmpty(current) && results.Count < MaxAddressSuggestions)
        {
            if (IsAddressPathMatch(current, expandedQuery))
            {
                AddSuggestion(results, current);
            }
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (results.Count >= MaxAddressSuggestions) break;
                if (!drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                if (!IsAddressPathMatch(root, expandedQuery)) continue;
                AddSuggestion(results, root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Drive enumeration is optional; keep the rest of the list.
        }

        if (expandedQuery.Length > 0)
        {
            foreach (var pair in ShellFallbackNames)
            {
                if (results.Count >= MaxAddressSuggestions) break;
                if (!pair.Value.Contains(expandedQuery, StringComparison.OrdinalIgnoreCase)) continue;
                AddShellSuggestion(results, pair.Key, pair.Value);
            }
        }

        return results;
    }

    private static void AddDirectoryMatches(
        List<AddressSuggestion> results,
        string query,
        CancellationToken token)
    {
        if (query.Length == 0) return;
        if (query.Length == 2 && char.IsLetter(query[0]) && query[1] == ':')
        {
            query += Path.DirectorySeparatorChar;
        }

        string? directory;
        string prefix;
        if (EndsWithDirectorySeparator(query))
        {
            directory = query;
            prefix = string.Empty;
        }
        else
        {
            directory = Path.GetDirectoryName(query);
            prefix = Path.GetFileName(query);
        }
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        var directories = new List<string>();
        var files = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                token.ThrowIfCancellationRequested();
                if (!FileNameStartsWith(path, prefix)) continue;
                directories.Add(path);
                if (directories.Count >= MaxAddressSuggestions) break;
            }
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                if (!FileNameStartsWith(path, prefix)) continue;
                files.Add(path);
                if (files.Count >= MaxAddressSuggestions) break;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            // A protected or disconnected directory should never break typing.
        }

        foreach (var path in directories.OrderBy(
                     path => Path.GetFileName(path),
                     StringComparer.OrdinalIgnoreCase))
        {
            if (results.Count >= MaxAddressSuggestions) return;
            AddSuggestion(results, path);
        }
        foreach (var path in files.OrderBy(
                     path => Path.GetFileName(path),
                     StringComparer.OrdinalIgnoreCase))
        {
            if (results.Count >= MaxAddressSuggestions) return;
            AddSuggestion(results, path);
        }
    }

    private static bool EndsWithDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            || path.EndsWith(Path.AltDirectorySeparatorChar);
    }

    private static bool FileNameStartsWith(string path, string prefix)
    {
        if (prefix.Length == 0) return true;
        var name = Path.GetFileName(path);
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAddressPathMatch(string path, string query)
    {
        if (query.Length == 0) return true;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return name.StartsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddSuggestion(List<AddressSuggestion> results, string path)
    {
        if (results.Count >= MaxAddressSuggestions || string.IsNullOrWhiteSpace(path)) return;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        if (results.Any(s => string.Equals(s.Path, normalized, StringComparison.OrdinalIgnoreCase))) return;
        if (!Directory.Exists(normalized) && !File.Exists(normalized)) return;

        var isFolder = Directory.Exists(normalized);
        var trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        results.Add(new AddressSuggestion(
            string.IsNullOrEmpty(name) ? normalized : name,
            normalized,
            isFolder));
    }

    private static void AddShellSuggestion(
        List<AddressSuggestion> results,
        string path,
        string name)
    {
        if (results.Count >= MaxAddressSuggestions) return;
        if (results.Any(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        results.Add(new AddressSuggestion(name, path, true));
    }

    private void ApplyAddressSuggestions(List<AddressSuggestion> suggestions)
    {
        _addressSuggestions = suggestions;
        _addressSuggestionChosen = false;
        AddressSuggestList.SelectedIndex = -1;
        if (suggestions.Count == 0)
        {
            AddressSuggestPopup.IsOpen = false;
            AddressSuggestList.ItemsSource = null;
            return;
        }
        AddressSuggestList.ItemsSource = suggestions;
        AddressSuggestPopup.IsOpen = true;
    }

    private void CloseAddressSuggestions()
    {
        _addressSuggestionsCts?.Cancel();
        _addressSuggestionsCts = null;
        AddressSuggestPopup.IsOpen = false;
        AddressSuggestList.ItemsSource = null;
        _addressSuggestions = new List<AddressSuggestion>();
        _addressSuggestionChosen = false;
    }

    private void MoveAddressSuggestionSelection(int delta)
    {
        if (_addressSuggestions.Count == 0) return;

        var current = AddressSuggestList.SelectedIndex;
        var next = current < 0
            ? (delta > 0 ? 0 : _addressSuggestions.Count - 1)
            : Math.Clamp(current + delta, 0, _addressSuggestions.Count - 1);
        AddressSuggestList.SelectedIndex = next;
        _addressSuggestionChosen = true;
        AddressSuggestList.ScrollIntoView(AddressSuggestList.SelectedItem);
    }

    /// <summary>
    /// Enter in the address box: keep the typed navigation for as long as it
    /// does not exactly match a suggestion, because pressing Enter anywhere in
    /// the list would otherwise silently override hand-typed paths.
    /// </summary>
    private bool TryAcceptAddressSuggestion()
    {
        if (!AddressSuggestPopup.IsOpen || !_addressSuggestionChosen) return false;
        return TryOpenAddressSuggestion(AddressSuggestList.SelectedItem as AddressSuggestion);
    }

    private void AddressSuggestList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (TryOpenAddressSuggestion(AddressSuggestList.SelectedItem as AddressSuggestion)) e.Handled = true;
    }

    private bool TryOpenAddressSuggestion(AddressSuggestion? suggestion)
    {
        if (suggestion is null) return false;
        CloseAddressSuggestions();
        ShowBreadcrumb(shouldEdit: false);
        if (suggestion.IsFolder)
        {
            NavigateActive(suggestion.Path);
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo(suggestion.Path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打开失败");
            }
        }
        return true;
    }

}

