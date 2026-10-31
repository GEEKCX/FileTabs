using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private System.Windows.Point _titleBarDragStart;
    private bool _isTitleBarDragArmed;
    private Bookmark? _dragBookmark;
    private bool _isDraggingBookmark;
    private bool _isClosing;
    private List<string> _recentlyClosed = new();
    private bool _everythingAvailable = true;
    private DispatcherTimer? _sessionSaveTimer;
    private Rect _normalWindowBounds;
    // Size-move loop state: a border drag resizes the shell view on every
    // tick, so the body is hidden until the drag ends (see WndProcHook).
    private bool _inSizeMove;
    private bool _shellViewsSuppressed;
    private int _sizeMoveStartClientWidth;
    private int _sizeMoveStartClientHeight;
    private List<AddressSuggestion> _addressSuggestions = new();
    private CancellationTokenSource? _addressSuggestionsCts;
    private bool _addressSuggestionChosen;
    private bool _isAddressEditing;
    private bool _isApplyingAppearanceSettings;
    private readonly DispatcherTimer _breadcrumbNavTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _tabLoadingWatchdog = new() { Interval = TimeSpan.FromSeconds(3) };
    // Polls the left button while a size-move loop is running; some drags end
    // without WM_EXITSIZEMOVE and must not leave the body collapsed.
    private readonly DispatcherTimer _sizeMoveWatchdog = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private BrowserTab? _loadingTab;
    private bool _loadingFallbackApplied;
    private BreadcrumbSegment? _pendingBreadcrumbSegment;
    private BrowserTab? _pendingBreadcrumbTab;
    private BreadcrumbSegment? _lastBreadcrumbClickSegment;
    private DateTime _lastBreadcrumbClickAt;
    private DateTime _addressEditEnteredAt;
    private bool _addressOpenedByBreadcrumbClick;
    private string _addressDisplayText = string.Empty;

    public MainWindow()
    {
        StartupTiming.Mark("mainwindow-ctor-begin");
        InitializeComponent();
        StartupTiming.Mark("mainwindow-xaml-loaded");
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
        TabBar.SizeChanged += (_, _) => UpdateTabWidth();
        SizeChanged += (_, _) => TrackNormalWindowBounds();
        LocationChanged += (_, _) => TrackNormalWindowBounds();
        StateChanged += (_, _) => TrackNormalWindowBounds();
        Closing += (_, _) => FlushSessionBeforeClose();
        Closing += (_, _) => SaveWindowPlacement();
        Closed += (_, _) => _captureService.Dispose();
        Closed += (_, _) => _isClosing = true;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        Deactivated += (_, _) => CancelBreadcrumbInteraction();
        SourceInitialized += (_, _) => ((HwndSource?)PresentationSource.FromVisual(this))?.AddHook(WndProcHook);

        ProbeEverythingAvailability();
        _breadcrumbNavTimer.Tick += (_, _) => NavigatePendingBreadcrumb();
        _tabLoadingWatchdog.Tick += (_, _) => HandleTabLoadingTimeout();
        _sizeMoveWatchdog.Tick += (_, _) => HandleSizeMoveWatchdog();
        // Coalesce rapid save triggers (navigation, tab switch, drag, close).
        _sessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _sessionSaveTimer.Tick += (_, _) => SaveCurrentSession();
        _captureService.NewExplorerWindow += OnExplorerWindowCaptured;
        UpdatePasteButton();
        RestoreWindowPlacement();
        StartupTiming.Mark("mainwindow-ctor-end");
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        StartupTiming.Mark("first-render");
        StartupTiming.Flush();
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
                ToolTip = tab.CurrentPath,
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
    private const double MenuButtonSlotWidth = 34;   // menu button 30 + right margin 4
    private const double NewTabButtonSlotWidth = 30; // new-tab button 28 + left margin 2
    private const double TabMinWidth = 64;
    private const double TabMaxWidth = 240;

    public static readonly DependencyProperty TabWidthProperty = DependencyProperty.Register(
        nameof(TabWidth), typeof(double), typeof(MainWindow),
        new PropertyMetadata(TabMaxWidth + 2 * TabScoopWidth));

    /// <summary>统一后的单块标签宽度，绑定在标签模板上。含左右各 8px 的反向外圆弧落位。</summary>
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
    /// 标签统一宽度：按标签区宽度均分，限制在 64~240。标签条宽度取实际内容
    /// 宽度，让 + 紧跟最后一块标签；内容放不下时收窄标签条，把 + 留在右端。
    /// </summary>
    private void UpdateTabWidth()
    {
        var tabBarWidth = TabBar.ActualWidth;
        if (tabBarWidth <= 0) return;
        var stripSpace = tabBarWidth - MenuButtonSlotWidth - NewTabButtonSlotWidth;
        if (stripSpace <= 0) return;

        var count = Math.Max(1, _tabs.Count);
        var bodyWidth = Math.Clamp(
            (stripSpace - TabStripLeftInset - TabScoopWidth) / count,
            TabMinWidth,
            TabMaxWidth);

        // TabWidth 是标签项目的宽度，比标签体左右各宽 8px 给反向外圆弧留位；
        // 标签体本身靠项目上的 Margin="8,0" 内缩，所以两块标签体依然紧挨着。
        TabWidth = bodyWidth + 2 * TabScoopWidth;

        var contentWidth = TabStripLeftInset + (_tabs.Count * bodyWidth) + TabScoopWidth;
        TabStrip.Width = Math.Min(contentWidth, stripSpace);
    }

    // ---------- size-move loop ----------

    // A border drag resizes the hosted shell view on every tick. Explorer's
    // DirectUI relayout costs tens of milliseconds and runs on the UI thread,
    // so WPF cannot repaint the frame underneath the HwndHost -- the airspace
    // hole then shows the desktop until the shell paints again. Collapsing the
    // body for the duration of the loop keeps the HwndHost out of layout (no
    // per-tick SetWindowPos reaches the shell), leaves the window background,
    // which WPF paints in a few hundred microseconds, and the shell relayouts
    // once when the drag ends.
    private void BeginSizeMove(IntPtr hwnd)
    {
        _inSizeMove = true;
        _shellViewsSuppressed = false;
        if (GetClientRect(hwnd, out var client))
        {
            _sizeMoveStartClientWidth = client.Right - client.Left;
            _sizeMoveStartClientHeight = client.Bottom - client.Top;
        }
        _sizeMoveWatchdog.Start();
    }

    private void SuppressShellViews()
    {
        if (_shellViewsSuppressed) return;
        _shellViewsSuppressed = true;
        ViewContainer.Visibility = Visibility.Collapsed;
    }

    private void EndSizeMove()
    {
        _inSizeMove = false;
        _sizeMoveWatchdog.Stop();
        if (!_shellViewsSuppressed) return;
        _shellViewsSuppressed = false;
        ViewContainer.Visibility = Visibility.Visible;
    }

    // A drag that is interrupted (the shell swallowed the mouse-up, a modal
    // dialog stole the loop) never delivers WM_EXITSIZEMOVE. The left button
    // being up is the reliable signal that the loop is over.
    private void HandleSizeMoveWatchdog()
    {
        if (!_inSizeMove) return;
        if (IsLeftButtonDown()) return;
        EndSizeMove();
    }

    private static bool IsLeftButtonDown()
        => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

    private const int VkLeftButton = 0x01;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect32 rect);

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SIZE = 0x0005;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;

        if (_isClosing) return IntPtr.Zero;

        if (msg == WM_ENTERSIZEMOVE)
        {
            BeginSizeMove(hwnd);
            return IntPtr.Zero;
        }

        if (msg == WM_SIZE)
        {
            // lParam carries the new client size; a drag that only moves the
            // window keeps it unchanged, so the body is hidden on the first
            // real resize tick, before WPF lays out the shell host.
            if (_inSizeMove)
            {
                if (!_shellViewsSuppressed)
                {
                    var width = (int)(lParam.ToInt64() & 0xFFFF);
                    var height = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                    if (width != _sizeMoveStartClientWidth || height != _sizeMoveStartClientHeight)
                    {
                        SuppressShellViews();
                    }
                }
            }
            else if (_shellViewsSuppressed)
            {
                // A size change outside the loop means the exit message was
                // lost (drag interrupted by the shell). Never leave the body
                // collapsed.
                EndSizeMove();
            }
            return IntPtr.Zero;
        }

        if (msg == WM_EXITSIZEMOVE)
        {
            EndSizeMove();
            return IntPtr.Zero;
        }

        if (msg != WM_KEYDOWN) return IntPtr.Zero;

        // GetKeyState, not Keyboard.Modifiers: this runs before WPF's input
        // system has processed the key, so the WPF state can still be stale.
        var control = (GetKeyState(VkControlKey) & 0x8000) != 0;
        if (!control) return IntPtr.Zero;
        var shift = (GetKeyState(VkShiftKey) & 0x8000) != 0;
        if (HandleWindowShortcut(ToKey((int)wParam), shift)) handled = true;
        return IntPtr.Zero;
    }

    private static bool RefreshTab(BrowserTab tab, ShellObject location)
    {
        if (tab.Browser is not { } browser) return false;
        browser.Navigate(location);
        return true;
    }

    private void Startup()
    {
        StartupTiming.Mark("startup-begin");
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
        StartupTiming.Mark("session-restored");
        ApplyCaptureExplorerSetting();
        UpdateTitleBarLayout();
        UpdateTabWidth();
        StartupTiming.Mark("startup-end");
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

        // Intersecting is not enough: a saved offset with a full-width window
        // pushes the caption buttons past the screen edge, so clamp the whole
        // frame into the virtual screen.
        left = Math.Clamp(left, virtualBounds.Left, virtualBounds.Right - width);
        top = Math.Clamp(top, virtualBounds.Top, virtualBounds.Bottom - height);

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
        StartupTiming.Mark("theme-apply-begin");
        ThemeManager.Apply(parsedTheme);
        StartupTiming.Mark("theme-apply-end");
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

    private BrowserTab CreateTab(string path, string? title = null)
    {
        var tab = new BrowserTab(path, title);
        tab.NavigationCompleted += (_, _) =>
        {
            if (ReferenceEquals(_active, tab))
            {
                // The host stayed hidden behind the loading overlay; reveal it
                // only once the shell has content to paint.
                if (tab.Host is { } host) host.Visibility = Visibility.Visible;
                SyncNavigationBar();
                HideTabLoadingOverlay(tab);
                RecordRecentLocation(tab.CurrentLocation);
            }
            StartupTiming.Mark($"navigation-complete {tab.CurrentPath}");
            StartupTiming.Flush();
            MarkSessionDirty();
        };
        tab.NavigationStateChanged += (_, _) =>
        {
            if (ReferenceEquals(_active, tab)) SyncNavigationBar();
        };
        tab.SelectionStateChanged += (_, _) =>
        {
            if (!ReferenceEquals(_active, tab)) return;
            UpdateStatusBar();
            SyncViewMenuChecks();
            UpdateRenameButton();
        };
        // Alt+Left / Alt+Right typed inside the native shell view; the WPF
        // key path cannot see those because the HwndHost owns the focus.
        tab.TabSwitchRequested += CycleTabBy;
        // Ctrl shortcuts have the same problem, one step worse: the shell also
        // binds most of the same chords, so BrowserTab reports and swallows.
        tab.ShellShortcutRequested += HandleShellShortcut;
        _tabs.Add(tab);
        return tab;
    }

    private void AddTab(string path)
    {
        var tab = CreateTab(path);
        ActivateTab(tab);
    }

    private void EnsureTabMaterialized(BrowserTab tab)
    {
        if (tab.IsMaterialized || _isClosing) return;
        if (!ReferenceEquals(_active, tab)) return;

        try
        {
            tab.Materialize();
            StartupTiming.Mark($"tab-materialize {tab.Title}");
            if (tab.Host is { } host)
            {
                ViewContainer.Children.Add(host);
                host.Visibility = Visibility.Hidden;
            }
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(tab.StartInitialNavigation));
        }
        catch (Exception ex)
        {
            AppStorage.Log($"Tab materialization failed for {tab.CurrentPath}: {ex}");
            if (ReferenceEquals(_active, tab))
            {
                _tabLoadingWatchdog.Stop();
                _loadingTab = tab;
                TabLoadingTitle.Text = "无法打开此标签";
                TabLoadingHint.Text = ex.Message;
            }
        }
    }

    private void ShowTabLoadingOverlay(BrowserTab tab)
    {
        if (!ReferenceEquals(_loadingTab, tab))
        {
            _loadingTab = tab;
            _loadingFallbackApplied = false;
            TabLoadingTitle.Text = tab.Title;
            TabLoadingHint.Text = "正在加载文件视图…";
        }
        TabLoadingOverlay.Visibility = Visibility.Visible;
        StartupTiming.Mark($"overlay-show {tab.Title}");
        _tabLoadingWatchdog.Stop();
        _tabLoadingWatchdog.Start();
    }

    private void HideTabLoadingOverlay(BrowserTab? tab = null)
    {
        if (tab is not null && !ReferenceEquals(_loadingTab, tab)) return;
        _tabLoadingWatchdog.Stop();
        StartupTiming.Mark($"overlay-hide {_loadingTab?.Title} fallback={_loadingFallbackApplied}");
        _loadingTab = null;
        _loadingFallbackApplied = false;
        TabLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void HandleTabLoadingTimeout()
    {
        _tabLoadingWatchdog.Stop();
        var tab = _loadingTab;
        if (tab is null || !ReferenceEquals(_active, tab))
        {
            HideTabLoadingOverlay();
            return;
        }
        if (tab.HasCompletedNavigation)
        {
            HideTabLoadingOverlay();
            return;
        }
        if (_loadingFallbackApplied)
        {
            // Even This PC did not finish; keep the overlay so the empty HWND
            // area is never mistaken for an empty folder.
            TabLoadingTitle.Text = "无法打开此位置";
            TabLoadingHint.Text = "保存的路径暂时不可用，可尝试切换标签或在地址栏输入其他位置。";
            return;
        }

        _loadingFallbackApplied = true;
        StartupTiming.Mark($"overlay-fallback {tab.Title}");
        TabLoadingHint.Text = "保存的路径暂时不可用，正在打开此电脑…";
        try
        {
            EnsureTabMaterialized(tab);
            tab.MarkUnavailable();
            tab.NavigateTo(BrowserTab.ThisPc);
            // If materialization had not reached the first navigation yet,
            // NavigateTo only updated the target; make sure it starts now.
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(tab.StartInitialNavigation));
        }
        catch (Exception ex)
        {
            AppStorage.Log($"Fallback navigation failed for {tab.CurrentPath}: {ex}");
            HideTabLoadingOverlay();
            return;
        }
        _tabLoadingWatchdog.Start();
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
            var parsing = t.CurrentPath;
            return !string.IsNullOrEmpty(parsing) && string.Equals(
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
        CancelBreadcrumbInteraction();
        BodyRow.Height = new GridLength(1, GridUnitType.Star);
        HistoryRow.Height = new GridLength(0);
        HistoryPanel.Visibility = Visibility.Collapsed;
        foreach (var t in _tabs)
        {
            t.IsActive = ReferenceEquals(t, tab);
            if (t.Host is { } host)
            {
                host.Visibility = t.IsActive && t.HasCompletedNavigation
                    ? Visibility.Visible
                    : Visibility.Hidden;
            }
        }
        _active = tab;
        UpdateTabCount();
        SyncNavigationBar();
        UpdateStatusBar();
        MarkSessionDirty();
        StartupTiming.Mark($"tab-activate {tab.Title} materialized={tab.IsMaterialized} completed={tab.HasCompletedNavigation}");

        if (!tab.HasCompletedNavigation)
        {
            ShowTabLoadingOverlay(tab);
            if (!tab.IsMaterialized)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => EnsureTabMaterialized(tab)));
            }
            else
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(tab.StartInitialNavigation));
            }
        }
        else
        {
            HideTabLoadingOverlay();
        }
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
        var closedPath = tab.SessionPath;
        _tabs.Remove(tab);
        if (tab.Host is { } host) ViewContainer.Children.Remove(host);
        if (ReferenceEquals(_loadingTab, tab)) HideTabLoadingOverlay();
        tab.Dispose();

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
        CancelBreadcrumbInteraction();
        _isAddressEditing = false;
        var tab = _active;
        var log = tab?.Browser?.NavigationLog;
        var location = tab?.CurrentLocation;
        var parsing = tab?.CurrentPath ?? string.Empty;
        // Virtual shell folders (This PC, Network) have no file path; their
        // parsing name is a GUID. Show the friendly name instead, and keep the
        // GUID in Tag so Copy/Edit can still navigate to the real location.
        AddressBox.Text = ShellDisplayName(parsing, location?.Name ?? tab?.Title);
        _addressDisplayText = AddressBox.Text;
        AddressBox.Tag = parsing;
        UpdateBreadcrumbs(parsing, location?.Name ?? tab?.Title);
        BackButton.IsEnabled =
            tab?.Browser is not null &&
            (log?.CanNavigateBackward ?? false);
        ForwardButton.IsEnabled =
            tab?.Browser is not null &&
            (log?.CanNavigateForward ?? false);
        NewFolderButton.IsEnabled = CanModifyCurrentFolder();
        UpdatePasteButton();
        UpdateRenameButton();
    }

    /// <summary>
    /// Rename needs exactly one real file-system item selected; synced on
    /// navigation and selection changes.
    /// </summary>
    private void UpdateRenameButton()
    {
        if (_active is { HasCompletedNavigation: true } tab
            && tab.Browser is { } browser)
        {
            try
            {
                var count = 0;
                foreach (var item in browser.SelectedItems)
                {
                    if (item is ShellObject shellItem
                        && Path.IsPathRooted(shellItem.ParsingName))
                    {
                        count++;
                        if (count > 1) break;
                    }
                }
                RenameButton.IsEnabled = count == 1;
                return;
            }
            catch (COMException)
            {
                // The shell view is between item collections mid-navigation.
            }
        }
        RenameButton.IsEnabled = false;
    }

    private void UpdateBreadcrumbs(string parsingPath, string? shellName = null)
    {
        var displayText = ShellDisplayName(parsingPath, shellName);
        BreadcrumbItems.ItemsSource = BuildBreadcrumbs(parsingPath, displayText, shellName);
        ShowBreadcrumb(shouldEdit: false);
    }

    private sealed record BreadcrumbSegment(
        string Path,
        string Label,
        int TextStart,
        int TextLength);

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

    private static List<BreadcrumbSegment> BuildBreadcrumbs(
        string parsingPath,
        string displayText,
        string? shellName = null)
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
        // UNC shares are filesystem paths too, but resolving their display
        // name through the shell hits the network; build the segments from
        // the path text instead.
        var isUncPath = parsingPath.StartsWith(@"\\", StringComparison.Ordinal);
        if (!isDrivePath && !isUncPath)
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
            result.Add(new BreadcrumbSegment(parsingPath, label, 0, displayText.Length));
            return result;
        }

        var segments = parsingPath.Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var accumulated = isUncPath ? @"\\" : string.Empty;
        var searchStart = isUncPath ? 2 : 0;
        foreach (var segment in segments)
        {
            if (isUncPath)
            {
                accumulated = accumulated.EndsWith(@"\", StringComparison.Ordinal)
                    ? accumulated + segment
                    : accumulated + Path.DirectorySeparatorChar + segment;
                var start = Math.Min(searchStart, displayText.Length);
                var length = Math.Min(segment.Length, displayText.Length - start);
                result.Add(new BreadcrumbSegment(accumulated, segment, start, length));
                searchStart = start + length + 1;
                continue;
            }

            accumulated = accumulated.Length == 0
                ? segment
                : accumulated + Path.DirectorySeparatorChar + segment;
            var label = segment;
            int textStart;
            int textLength;
            if (accumulated.Length == 2)
            {
                // Root like "C:" -> show as "C:\"
                label = segment + Path.DirectorySeparatorChar;
                textStart = 0;
                // "C:" (no trailing separator) is shorter than the "C:\" label.
                textLength = Math.Min(label.Length, displayText.Length);
                searchStart = textStart + textLength;
            }
            else
            {
                textStart = Math.Min(searchStart, displayText.Length);
                textLength = Math.Min(segment.Length, displayText.Length - textStart);
                searchStart = textStart + textLength + 1;
            }
            result.Add(new BreadcrumbSegment(accumulated, label, textStart, textLength));
        }
        return result;
    }

    private void ShowBreadcrumb(bool shouldEdit)
    {
        if (shouldEdit)
        {
            EnterAddressEdit();
        }
        else
        {
            ExitAddressEdit();
        }
    }

    private void EnterAddressEdit(
        BreadcrumbSegment? segment = null,
        bool showSuggestions = true,
        bool preserveAllOnDoubleClick = false)
    {
        CancelBreadcrumbInteraction();
        // Escaping an edit leaves its text behind in the hidden TextBox; a new
        // edit must start from the location actually displayed in the bar.
        if (!_isAddressEditing && _addressDisplayText.Length > 0)
        {
            AddressBox.Text = _addressDisplayText;
        }
        _isAddressEditing = true;
        _addressEditEnteredAt = DateTime.UtcNow;
        _addressOpenedByBreadcrumbClick = preserveAllOnDoubleClick;
        BreadcrumbBar.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;

        // Focus and selection must wait for layout; doing it while the TextBox
        // is still becoming visible lets the click that opened it reset the
        // selection.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_isAddressEditing || !IsLoaded) return;
            AddressBox.Focus();
            if (segment is { TextLength: > 0 })
            {
                var start = Math.Clamp(segment.TextStart, 0, AddressBox.Text.Length);
                var length = Math.Clamp(segment.TextLength, 0, AddressBox.Text.Length - start);
                AddressBox.Select(start, length);
            }
            else
            {
                AddressBox.SelectAll();
            }
            if (showSuggestions) QueueAddressSuggestions(immediate: true);
        }));
    }

    private void ExitAddressEdit()
    {
        CancelBreadcrumbInteraction();
        _isAddressEditing = false;
        _addressOpenedByBreadcrumbClick = false;
        BreadcrumbBar.Visibility = Visibility.Visible;
        AddressBox.Visibility = Visibility.Collapsed;
        CloseAddressSuggestions();
    }

    private void BreadcrumbBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Single-click anywhere in the breadcrumb's empty area switches to the
        // editable text box; segment clicks stop propagation themselves.
        EnterAddressEdit(showSuggestions: true, preserveAllOnDoubleClick: true);
        e.Handled = true;
    }

    private void BreadcrumbPart_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BreadcrumbSegment segment }) return;

        var now = DateTime.UtcNow;
        var isDoubleClick = ReferenceEquals(segment, _lastBreadcrumbClickSegment)
                            && (now - _lastBreadcrumbClickAt).TotalMilliseconds <= 250;
        CancelBreadcrumbInteraction();

        if (isDoubleClick)
        {
            // The second click cancels the pending navigation and opens the
            // editor with only this path segment selected.
            EnterAddressEdit(segment, showSuggestions: false);
        }
        else
        {
            _lastBreadcrumbClickSegment = segment;
            _lastBreadcrumbClickAt = now;
            _pendingBreadcrumbSegment = segment;
            _pendingBreadcrumbTab = _active;
            _breadcrumbNavTimer.Start();
        }
        e.Handled = true;
    }

    private void NavigatePendingBreadcrumb()
    {
        _breadcrumbNavTimer.Stop();
        var segment = _pendingBreadcrumbSegment;
        var tab = _pendingBreadcrumbTab;
        _pendingBreadcrumbSegment = null;
        _pendingBreadcrumbTab = null;
        _lastBreadcrumbClickSegment = null;
        _lastBreadcrumbClickAt = default;

        if (segment is null || tab is null || !ReferenceEquals(tab, _active)) return;
        NavigateActive(segment.Path);
    }

    private void CancelBreadcrumbInteraction()
    {
        _breadcrumbNavTimer.Stop();
        _pendingBreadcrumbSegment = null;
        _pendingBreadcrumbTab = null;
        _lastBreadcrumbClickSegment = null;
        _lastBreadcrumbClickAt = default;
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A new click anywhere invalidates a title-bar drag armed by a press
        // whose release we never saw (e.g. the pointer left the title bar).
        _isTitleBarDragArmed = false;

        // A click anywhere outside the address bar cancels a pending segment
        // navigation before its 250ms window expires.
        if (_pendingBreadcrumbSegment is not null && !BreadcrumbBar.IsMouseOver)
        {
            CancelBreadcrumbInteraction();
        }
    }

    private void AddressBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking the breadcrumb's empty area opens the editor with
        // everything selected; the second click lands in the TextBox and would
        // otherwise replace that selection with a single word.
        if (!_isAddressEditing || !_addressOpenedByBreadcrumbClick) return;
        if ((DateTime.UtcNow - _addressEditEnteredAt).TotalMilliseconds > 300) return;
        AddressBox.SelectAll();
        e.Handled = true;
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        // Prefer the raw parsing name so pasting still resolves virtual folders.
        var path = AddressBox.Tag as string ?? _active?.CurrentPath;
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
        if (_active is not { } tab) return;
        CloseAddressSuggestions();
        try
        {
            tab.NavigateTo(path);
            if (!tab.IsMaterialized)
            {
                ShowTabLoadingOverlay(tab);
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => EnsureTabMaterialized(tab)));
            }
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
        _recentlyClosed.Clear();
        if (session is { } loadedSession)
        {
            foreach (var p in loadedSession.RecentlyClosed.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                _recentlyClosed.Insert(0, p);
            }
        }

        var paths = session?.Tabs ?? new List<string>();
        // First run: take the tab set from 360 Folder if it has one.
        if (paths.Count == 0) paths = Import360.ImportSessionPaths();

        if (paths.Count == 0)
        {
            AddTab(AppSettings.Current.NewTabPath);
            return;
        }

        // Tabs stay metadata-only until first activation, so the window can
        // render without building shell HWNDs for every restored tab.
        // Titles and pinned flags are parallel to paths; older session files
        // have neither, and empty/missing entries fall back to the path name.
        var titles = session?.Titles;
        var pinnedFlags = session?.Pinned;
        var created = new List<BrowserTab>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(paths[i])) continue;
            var title = titles is not null && i < titles.Count ? titles[i] : null;
            var tab = CreateTab(paths[i], title);
            if (pinnedFlags is not null && i < pinnedFlags.Count) tab.IsPinned = pinnedFlags[i];
            created.Add(tab);
        }

        if (created.Count == 0)
        {
            AddTab(AppSettings.Current.NewTabPath);
            return;
        }

        var savedActive = session?.ActiveIndex ?? -1;
        var active = savedActive >= 0 && savedActive < created.Count ? created[savedActive] : created[0];
        ActivateTab(active);
    }

    private SessionData BuildSessionData()
    {
        var paths = new List<string>();
        var titles = new List<string>();
        var pinned = new List<bool>();
        var activeIndex = 0;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var path = _tabs[i].SessionPath;
            if (string.IsNullOrEmpty(path)) continue;
            if (ReferenceEquals(_tabs[i], _active)) activeIndex = paths.Count;
            paths.Add(path);
            titles.Add(_tabs[i].SessionTitle);
            pinned.Add(_tabs[i].IsPinned);
        }

        return new SessionData
        {
            Tabs = paths,
            ActiveIndex = activeIndex,
            Pinned = pinned,
            Titles = titles,
        };
    }

    private void RememberClosedPath(string path)
    {
        _recentlyClosed.RemoveAll(p =>
            string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentlyClosed.Insert(0, path);
        MarkSessionDirty();
    }

    private void MarkSessionDirty() => _sessionSaveTimer?.Start();

    private void StopSessionSaveTimer() => _sessionSaveTimer?.Stop();

    private void SaveCurrentSession(bool synchronous = false)
    {
        StopSessionSaveTimer();
        var data = BuildSessionData();
        data.RecentlyClosed = new List<string>(_recentlyClosed);
        if (synchronous)
        {
            // A queued write may still be in flight; let it land first so the
            // final payload is the newest one on disk.
            AppStorage.WaitForPendingWrites(TimeSpan.FromSeconds(2));
            AppStorage.SaveSession(data);
        }
        else
        {
            AppStorage.SaveSessionAsync(data);
        }
    }

    private void FlushSessionBeforeClose()
    {
        StopSessionSaveTimer();
        SaveCurrentSession(synchronous: true);
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
        // The toolbar file rename switches the popup to Bottom placement on
        // the button; reset so the bookmark rename still follows the mouse.
        RenamePopup.PlacementTarget = null;
        RenamePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
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
        _shellRenameTarget = null;
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitRename();
        else if (e.Key == Key.Escape)
        {
            RenamePopup.IsOpen = false;
            _renameTarget = null;
            _shellRenameTarget = null;
        }
    }

    private void CommitRename()
    {
        var name = RenameBox.Text.Trim();
        if (_shellRenameTarget is { } shellPath && name.Length > 0)
        {
            if (!string.Equals(name, Path.GetFileName(shellPath), StringComparison.Ordinal))
            {
                RunShellOperation(() => ShellFileOperations.Rename(shellPath, name),
                    "重命名失败");
                RefreshActive();
            }
        }
        else if (_renameTarget is { } b && name.Length > 0)
        {
            b.Name = name;
            AppStorage.SaveBookmarks(_bookmarks);
            // Force the item template to re-read Name.
            var index = _bookmarks.IndexOf(b);
            _bookmarks[index] = b;
        }
        RenamePopup.IsOpen = false;
        _renameTarget = null;
        _shellRenameTarget = null;
    }

    // ---------- command handlers ----------

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab(AppSettings.Current.NewTabPath);

    // ---------- file commands ----------

    private string? CurrentFolderPath()
    {
        // Directory.Exists on an offline UNC share or a disconnected mapped
        // drive blocks for the network timeout. Trust the loaded shell view
        // instead; if the folder vanished, the file command reports the error.
        if (_active is not { HasCompletedNavigation: true } tab) return null;
        var path = tab.CurrentPath;
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith("::", StringComparison.Ordinal))
        {
            return null;
        }
        return path;
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
            _active?.Browser?.Navigate(ShellObject.FromParsingName(folder));
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
        if (_active is not { } tab || tab.Browser is not { } browser) return;
        var paths = new System.Collections.Specialized.StringCollection();
        foreach (var item in browser.SelectedItems)
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

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        // Multi-selection rename is ambiguous (pattern rename is a separate
        // feature); restrict the toolbar entry to exactly one item, matching
        // what the properties button does.
        var sources = SelectedShellPaths();
        if (sources.Count != 1) return;
        var target = sources[0];
        var originalName = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar));
        _shellRenameTarget = target;
        RenameBox.Text = originalName;
        RenamePopup.PlacementTarget = RenameButton;
        RenamePopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        RenamePopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RenameBox.Focus();
            // Select the stem without the extension, like Explorer does on F2.
            var withoutExtension = Path.GetFileNameWithoutExtension(originalName);
            RenameBox.Select(0, withoutExtension.Length);
        }), DispatcherPriority.Input);
    }

    /// <summary>
    /// Target path while the toolbar rename popup is open. Non-null also
    /// routes CommitRename to the shell item rename instead of the bookmark
    /// rename that shares the same popup.
    /// </summary>
    private string? _shellRenameTarget;

    private List<string> SelectedShellPaths()
    {
        var result = new List<string>();
        if (_active is not { } tab || tab.Browser is not { } browser) return result;
        try
        {
            foreach (var item in browser.SelectedItems)
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
        if (_active is { } tab && tab.CurrentLocation is { } location)
        {
            RefreshTab(tab, location);
        }
    }

    private void RunShellOperation(Action operation, string failureTitle)
    {
        try
        {
            operation();
            // Copy/move/delete/rename change folder contents; drop cached
            // sizes so the status bar recomputes instead of showing stale
            // numbers until the TTL lapses.
            FolderSizeCache.Invalidate(null);
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
            if (tab.Browser is { } browser
                && browser.SelectedItems.Count == 1
                && browser.SelectedItems[0] is ShellObject item)
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
        OpenTerminalMenuItem.IsEnabled = _active?.CurrentLocation is not null;
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
        var current = _active?.Browser is { IsHandleCreated: true } browser
            ? browser.ContentOptions.ViewMode
            : ExplorerBrowserViewMode.Auto;
        var thumbnailSize = 0;
        if (_active?.Browser is { IsHandleCreated: true } activeBrowser)
        {
            try
            {
                thumbnailSize = activeBrowser.ContentOptions.ThumbnailSize;
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
        if (_active is not { } tab || tab.Browser is not { } browser)
        {
            StatusBar.Visibility = Visibility.Collapsed;
            StatusText.Text = string.Empty;
            StatusHintText.Text = string.Empty;
            return;
        }
        try
        {
            var items = browser.Items;
            var selected = browser.SelectedItems;
            var selectedCount = selected.Count;
            var folders = 0;
            var selectedFolderPaths = new List<string>();
            var allFolders = true;
            for (var i = 0; i < selectedCount; i++)
            {
                if (selected[i] is not { } item)
                {
                    allFolders = false;
                    break;
                }
                if (item is ShellFolder)
                {
                    folders++;
                    var parsing = item.ParsingName;
                    if (!string.IsNullOrWhiteSpace(parsing) && Path.IsPathRooted(parsing))
                    {
                        selectedFolderPaths.Add(parsing);
                    }
                    continue;
                }
                // The custom bar only shows for an all-folder selection (the
                // shell status bar covers sizes), so the first non-folder item
                // decides the outcome. Stop touching the shell collection —
                // Ctrl+A over thousands of files used to pay one COM property
                // read per item on the UI thread just to hide the bar.
                allFolders = false;
                break;
            }
            var totalCount = items.Count;
            // The shell status bar already covers the common cases: total item
            // count, item size and "N selected". Keep FileTabs' bar only when
            // the selection is entirely folders; that child count is the one
            // aggregate the native view does not show.
            if (selectedCount == 0 || !allFolders)
            {
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = string.Empty;
                StatusHintText.Text = string.Empty;
            }
            else
            {
                StatusBar.Visibility = Visibility.Visible;
                StatusText.Text = $"已选择 {selectedCount} 个项目";
                StatusHintText.Text = folders > 0
                    ? $"{totalCount} 个项目 · 含 {folders} 个文件夹"
                    : $"{totalCount} 个项目";
                BeginFolderSizeSummary(selectedFolderPaths);
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

    // Guard so a completed computation re-enters UpdateStatusBar exactly once
    // per selection change; the token changes whenever the selection does.
    private string _folderSizeRequestKey = string.Empty;

    /// <summary>
    /// Show the recursive size of the selected folders in the status bar.
    /// Cached sizes appear immediately; unknown ones kick off background
    /// computation and refresh the bar when done. Selection is captured in a
    /// key so a slow folder cannot overwrite a newer selection's text.
    /// </summary>
    private void BeginFolderSizeSummary(List<string> folderPaths)
    {
        if (folderPaths.Count == 0) return;
        var key = string.Join("|", folderPaths);
        _folderSizeRequestKey = key;

        long? total = 0;
        var pending = false;
        foreach (var folder in folderPaths)
        {
            var size = FolderSizeCache.TryGet(folder);
            if (size is null)
            {
                pending = true;
                FolderSizeCache.BeginCompute(folder);
                total = null;
            }
            else if (total is { } acc && size >= 0)
            {
                total = acc + size;
            }
        }

        if (total is { } known && known > 0)
        {
            StatusText.Text = $"已选择 {folderPaths.Count} 个文件夹 · {FormatByteSize((ulong)known)}";
        }
        else if (pending)
        {
            StatusText.Text = $"已选择 {folderPaths.Count} 个文件夹 · 计算大小中…";
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                // Poll briefly until every folder in this request is cached;
                // each completion also triggers a direct refresh below.
                for (var i = 0; i < 100; i++)
                {
                    await Task.Delay(150);
                    if (_folderSizeRequestKey != key) return; // selection moved on
                    if (_isClosing) return;
                    var allDone = folderPaths.All(f => FolderSizeCache.TryGet(f) is not null);
                    if (allDone)
                    {
                        UpdateStatusBar();
                        return;
                    }
                }
            }), DispatcherPriority.Background);
        }
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        var path = _active?.CurrentPath;
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
        _tabLoadingWatchdog.Stop();
        _loadingTab = null;
        foreach (var tab in _tabs.Reverse())
        {
            _tabs.Remove(tab);
            if (tab.Host is { } host) ViewContainer.Children.Remove(host);
            tab.Dispose();
        }

        foreach (var path in match.Tabs.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            CreateTab(path);
        }
        if (_tabs.Count == 0)
        {
            AddTab(AppSettings.Current.NewTabPath);
        }
        else
        {
            var active = Math.Min(Math.Max(match.ActiveIndex, 0), _tabs.Count - 1);
            ActivateTab(_tabs[active]);
        }
        match.LastUsedAt = DateTime.Now;
        var workspaces = AppStorage.LoadWorkspaces();
        var stored = workspaces.FirstOrDefault(w => string.Equals(w.Name, match.Name, StringComparison.Ordinal));
        if (stored != null) stored.LastUsedAt = DateTime.Now;
        AppStorage.SaveWorkspaces(workspaces);
    }

    private void TabStripBackground_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            AddTab(AppSettings.Current.NewTabPath);
            e.Handled = true;
            return;
        }

        // TabBar now spans the whole title bar so the caption buttons stay on
        // screen, which leaves WPF-UI's TitleBar with no HTCAPTION area to drag
        // (it treats the entire Header as content). Arm a drag on the bare
        // background only; tabs, the menu and the new-tab button keep theirs.
        if (e.ClickCount == 1 && ReferenceEquals(e.OriginalSource, TabBar))
        {
            _titleBarDragStart = e.GetPosition(this);
            _isTitleBarDragArmed = true;
        }
    }

    private bool TryStartTitleBarDrag(System.Windows.Input.MouseEventArgs e)
    {
        if (!_isTitleBarDragArmed) return false;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _isTitleBarDragArmed = false;
            return false;
        }

        var position = e.GetPosition(this);
        var moved =
            Math.Abs(position.X - _titleBarDragStart.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(position.Y - _titleBarDragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!moved) return true;

        _isTitleBarDragArmed = false;
        DragTitleBarWindow();
        return true;
    }

    private void DragTitleBarWindow()
    {
        if (WindowState == WindowState.Maximized)
        {
            // DragMove keeps a maximized window in place; restore it under the
            // cursor first, the way a native caption drag does.
            var cursor = Mouse.GetPosition(this);
            var horizontalRatio = ActualWidth > 0 ? cursor.X / ActualWidth : 0.5;
            var screen = PointToScreen(cursor);
            var dpi = VisualTreeHelper.GetDpi(this);
            WindowState = WindowState.Normal;
            Left = (screen.X / dpi.DpiScaleX) - (Width * horizontalRatio);
            Top = (screen.Y / dpi.DpiScaleY) - 16;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released between the move and the drag call.
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
        if (TryStartTitleBarDrag(e)) return;

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
            tabWidth = Math.Max(40, container.ActualWidth - 2 * TabScoopWidth - 12);
        var target = from + (int)Math.Floor(delta / tabWidth);
        // Never drag an unpinned tab into the pinned block.
        target = Math.Max(_tabs.Count(t => t.IsPinned), Math.Min(_tabs.Count - 1, target));
        if (target == from) return;

        _tabs.Move(from, target);
        _dragStart = e.GetPosition(this);
    }

    private void TabStrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isTitleBarDragArmed = false;
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
        AddTab(tab.CurrentPath);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _active?.NavigateHistory(NavigationLogDirection.Backward);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        _active?.NavigateHistory(NavigationLogDirection.Forward);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.CurrentLocation?.Parent is { } parent)
        {
            NavigateActive(parent.ParsingName);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_active is { } tab && tab.CurrentLocation is { } location)
        {
            RefreshTab(tab, location);
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
        _addressOpenedByBreadcrumbClick = false;
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
        e.Handled = HandleWindowShortcut(e.Key, shift);
    }

    /// <summary>
    /// The Ctrl shortcuts the window owns. Both the WPF key path and the
    /// native shell view's subclass route here: with focus inside the shell
    /// view WM_KEYDOWN never reaches this window, so BrowserTab reports the
    /// virtual key instead of the WPF Key. Returns whether the key was used.
    /// </summary>
    private bool HandleWindowShortcut(Key key, bool shift)
    {
        switch (key)
        {
            case Key.T when shift:
                ReopenClosedTab();
                return true;
            case Key.T:
                AddTab(AppSettings.Current.NewTabPath);
                return true;
            case Key.W when _active is { } tab:
                CloseTab(tab);
                return true;
            case Key.H:
                ShowHistoryPage();
                return true;
            case Key.F:
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;
            case Key.L:
                ShowBreadcrumb(shouldEdit: true);
                return true;
            case Key.S:
                SaveWorkspace_Click(this, new RoutedEventArgs());
                return true;
            case Key.O:
                OpenWorkspace_Click(this, new RoutedEventArgs());
                return true;
            case Key.F5:
            case Key.R:
                if (_active is { } activeTab && activeTab.CurrentLocation is { } location)
                {
                    RefreshTab(activeTab, location);
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Entry point for shortcuts typed inside the native shell view. Only the
    /// chords BrowserTab decided the window owns get here, so no extra
    /// filtering is needed.
    /// </summary>
    private void HandleShellShortcut(int virtualKey, bool shift)
        => HandleWindowShortcut(ToKey(virtualKey), shift);

    /// <summary>
    /// Maps the virtual keys the shortcuts use onto WPF's Key enum. Only the
    /// keys listed here can arrive from the shell subclass or the window hook,
    /// so the letter range plus F5 covers every case.
    /// </summary>
    private static Key ToKey(int virtualKey) => virtualKey switch
    {
        VkF5 => Key.F5,
        >= 'A' and <= 'Z' => (Key)((int)Key.A + (virtualKey - 'A')),
        _ => Key.None,
    };

    private const int VkF5 = 0x74;
    private const int VkControlKey = 0x11;
    private const int VkShiftKey = 0x10;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

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
        AppStorage.SaveRecentLocationsAsync(_recentLocations);
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
            var parsing = t.CurrentPath;
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
        var currentPath = _active?.CurrentPath;
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

