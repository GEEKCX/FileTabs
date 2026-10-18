using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Forms.Integration;
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

    private readonly string _initialPath;
    private bool _initialNavigationDone;

    private string _title = "New tab";
    private bool _isActive;

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

    public BrowserTab(string initialPath)
    {
        _initialPath = initialPath;

        Browser = new ExplorerBrowser { Dock = System.Windows.Forms.DockStyle.Fill };
        // Hide the shell's own command bar and navigation tree: the WPF chrome
        // (breadcrumb bar + bookmark bar) replaces both, and the Win10 shell
        // command bar clashes with the themed window. Shell view state still
        // respects the user's Explorer folder-type settings.
        Browser.NavigationOptions.PaneVisibility.Commands =
            Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        Browser.NavigationOptions.PaneVisibility.Navigation =
            Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        Browser.NavigationOptions.PaneVisibility.Preview = AppSettings.Current.ShowPreviewPane
            ? Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Show
            : Microsoft.WindowsAPICodePack.Controls.PaneVisibilityState.Hide;
        Host = new WindowsFormsHost { Child = Browser, Visibility = Visibility.Hidden };

        Browser.NavigationComplete += (_, e) =>
        {
            Title = !string.IsNullOrWhiteSpace(e.NewLocation?.Name)
                ? e.NewLocation!.Name
                : e.NewLocation?.ParsingName ?? string.Empty;
        };

        // Navigate once the host is loaded and its HWND exists;
        // navigating before that can be dropped by the control.
        Host.Loaded += OnHostLoaded;
    }

    private void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialNavigationDone) return;
        _initialNavigationDone = true;
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
