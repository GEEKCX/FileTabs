using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly ObservableCollection<BrowserTab> _tabs = new();
    private readonly ObservableCollection<Bookmark> _bookmarks = new();
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

    public MainWindow()
    {
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        BookmarkStrip.ItemsSource = _bookmarks;
        _bookmarks.CollectionChanged += (_, _) =>
            BookmarkEmptyHint.Visibility = _bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => Startup();
        Closing += (_, _) => FlushSessionBeforeClose();
        Closed += (_, _) => _isClosing = true;
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => ((HwndSource?)PresentationSource.FromVisual(this))?.AddHook(WndProcHook);

        ProbeEverythingAvailability();
        // Coalesce rapid save triggers (navigation, tab switch, drag, close).
        _sessionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _sessionSaveTimer.Tick += (_, _) => SaveCurrentSession();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // FluentWindow removes the caption (CaptionHeight=0) so Mica can bleed
        // through. Restore a 40px caption band so the empty area of the tab
        // strip drags/maximizes like a normal title bar, while the interactive
        // controls opt back in via IsHitTestVisibleInChrome in XAML.
        base.OnSourceInitialized(e);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(4),
            GlassFrameThickness = new Thickness(-1),
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

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        if (msg != WM_KEYDOWN || _isClosing) return IntPtr.Zero;

        const int VK_T = 0x54;
        const int VK_W = 0x57;
        var key = (int)wParam;
        var control = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        var shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        if (!control || key != VK_T && key != VK_W) return IntPtr.Zero;

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
        RestoreSession();
    }

    private void ApplyAppearanceSettings()
    {
        BookmarkBar.Visibility = AppSettings.Current.ShowBookmarkBar
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!Enum.TryParse<AppTheme>(AppSettings.Current.Theme, out var parsedTheme))
        {
            parsedTheme = AppTheme.FollowSystem;
        }
        ThemeManager.Apply(parsedTheme);
        // Make the empty area around the shell control match the theme; the
        // shell-drawn file list inside it still follows the system theme.
        Background = new SolidColorBrush(
            ((SolidColorBrush)FindResource("Fluent.Background")).Color);
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
        _tabs.Add(tab);
        ViewContainer.Children.Add(tab.Host);
        ActivateTab(tab);
    }

    private void ActivateTab(BrowserTab tab)
    {
        foreach (var t in _tabs)
        {
            t.IsActive = ReferenceEquals(t, tab);
            t.Host.Visibility = t.IsActive ? Visibility.Visible : Visibility.Hidden;
        }
        _active = tab;
        SyncNavigationBar();
        MarkSessionDirty();
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

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
        var log = _active?.Browser.NavigationLog;
        var location = log?.CurrentLocation;
        var parsing = location?.ParsingName ?? string.Empty;
        AddressBox.Text = parsing;
        UpdateBreadcrumbs(parsing);
        BackButton.IsEnabled = log?.CanNavigateBackward ?? false;
        ForwardButton.IsEnabled = log?.CanNavigateForward ?? false;
    }

    private void UpdateBreadcrumbs(string parsingPath)
    {
        BreadcrumbItems.ItemsSource = BuildBreadcrumbs(parsingPath);
        ShowBreadcrumb(shouldEdit: false);
    }

    private sealed record BreadcrumbSegment(string Path, string Label);

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

    private static List<BreadcrumbSegment> BuildBreadcrumbs(string parsingPath)
    {
        var result = new List<BreadcrumbSegment>();
        if (string.IsNullOrEmpty(parsingPath))
        {
            return result;
        }

        // Shell locations like "::{20D04FE0-...}" (This PC) are not drivable
        // paths; resolve their display name once and show them as a single
        // breadcrumb segment.
        if (parsingPath.Length < 2 || parsingPath[1] != ':')
        {
            var label = "Shell 位置";
            if (ShellFallbackNames.TryGetValue(parsingPath, out var known))
            {
                label = known;
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
        BreadcrumbBar.Visibility = shouldEdit ? Visibility.Collapsed : Visibility.Visible;
        AddressBox.Visibility = shouldEdit ? Visibility.Visible : Visibility.Collapsed;
        if (shouldEdit)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
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
        var path = _active?.Browser.NavigationLog.CurrentLocation?.ParsingName;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Clipboard.SetText(path);
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
        var activeIndex = 0;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var path = _tabs[i].Browser.NavigationLog.CurrentLocation?.ParsingName;
            if (string.IsNullOrEmpty(path)) continue;
            if (ReferenceEquals(_tabs[i], _active)) activeIndex = paths.Count;
            paths.Add(path);
        }

        return new SessionData
        {
            Tabs = paths,
            ActiveIndex = activeIndex,
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
        _bookmarks.Add(new Bookmark { Name = name, Path = path });
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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = this };
        window.ShowDialog();
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

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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

    private void TabStrip_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed) return;
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
        target = Math.Max(0, Math.Min(_tabs.Count - 1, target));
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

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab }) CloseTab(tab);
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

    private void SearchIcon_Click(object sender, RoutedEventArgs e) => SearchBox.Focus();

    private void Go_Click(object sender, RoutedEventArgs e) => NavigateActive(AddressBox.Text);

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateActive(AddressBox.Text);
            ShowBreadcrumb(shouldEdit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ShowBreadcrumb(shouldEdit: false);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
                        OpenSearchPanel(false);
                        return;
                    }
                    SearchResults.ItemsSource = results;
                    SearchResultCount.Text = $"{results.Count} 个结果";
                    OpenSearchPanel(results.Count > 0);
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
                    OpenSearchPanel(false);
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

    private EverythingClient.SortMode _searchSort = EverythingClient.SortMode.ModifiedDescending;

    private void OpenSearchPanel(bool focusList)
    {
        SearchResultsPanel.Visibility = Visibility.Visible;
        SearchPanelSplitter.Visibility = Visibility.Visible;
        SearchResultsPanel.Width = 300;
        SearchResults.Focus();
    }

    private void CloseSearchPanel()
    {
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        SearchPanelSplitter.Visibility = Visibility.Collapsed;
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
}

