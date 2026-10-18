using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
// WinForms (UseWindowsForms) and WPF both define Application; WPF wins here.
using Application = System.Windows.Application;

namespace FileTabs;

public enum AppTheme
{
    Light,
    Dark,
    FollowSystem,
}

/// <summary>
/// Swaps WPF UI's light and dark theme dictionaries. Every chrome color in the
/// XAML is a DynamicResource reference to a WPF UI theme key, so replacing the
/// dictionary repaints the whole app without a second palette to maintain.
/// </summary>
public static class ThemeManager
{
    // uxtheme undocumented API constants. AllowDark follows IsDarkActive();
    // Default restores the current system mode.
    private const uint PreferredAppModeDefault = 0;
    private const uint PreferredAppModeAllowDark = 1;

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
    private static extern int SetPreferredAppMode(uint mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    public static void Apply(AppTheme theme)
    {
        var dark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => IsSystemDark(),
        };

        // The backdrop must stay None: WPF UI implements Mica/Acrylic by
        // extending the DWM glass frame over the whole window, and the hosted
        // shell view then paints its item labels through the glass text path
        // (white text on the white list background).
        ApplicationThemeManager.Apply(
            dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.None);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsDarkActive()
        => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    /// <summary>
    /// Undocumented uxtheme ordinal 133. Marks a native window so its visual
    /// style (including DirectUI shell views) may choose dark resources.
    /// </summary>
    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

    /// <summary>
    /// Toggle the embedded shell view (Explorer file list) between light and
    /// dark. Uses undocumented but stable uxtheme ordinal 135 plus
    /// the compatible explorer theme; DirectUI needs the per-window opt-in.
    /// </summary>
    public static void ApplyShellTheme(IntPtr shellHwnd)
    {
        var dark = IsDarkActive();
        if (shellHwnd == IntPtr.Zero) return;

        SetPreferredAppMode(dark ? PreferredAppModeAllowDark : PreferredAppModeDefault);
        FlushMenuThemes();
        AllowDarkModeForWindow(shellHwnd, dark);
        SetWindowTheme(shellHwnd, dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    /// <summary>
    /// Make WinForms content (the ExplorerBrowser control host) blend into the
    /// themed chrome while shell paints its own file list background.
    /// </summary>
    public static System.Drawing.Color ShellHostBackColor
        => Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] is SolidColorBrush brush
            ? System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B)
            : System.Drawing.Color.White;
}
