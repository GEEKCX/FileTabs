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

        // 浅色主题用 360 的浅蓝灰，但深色主题保留 FileTabs 自己的暗色表面；
        // 两套主题共用同一组资源键，切换时重绘整个标题区。
        var chrome = dark
            ? new ChromePalette("#26313C", "#334252", "#3E4F61", "#26313C", "#34424F",
                "#222C36", "#303D4A", "#465567", "#1D252E", "#1D252E", "#E8EDF2", "#A9B4BF",
                "#2D3843", "#3B4856")
            : new ChromePalette("#DFE8F1", "#D0DCE9", "#C2D0DF", "#DFE8F1", "#FFFFFF",
                "#EAF0F6", "#D9E4EF", "#C4D2E0", "#FFFFFF", "#F7FAFC", "#1F2329", "#5F6670",
                "#F7F9FB", "#E3E9EF");
        // 360 的标题区是一整块浅蓝灰，标签间隙与背景同色。
        SetBrush("FileTabs.ChromeFillBrush", chrome.Chrome);
        SetBrush("FileTabs.TitleFillBrush", chrome.Chrome);
        SetBrush("FileTabs.ChromeHoverBrush", chrome.Hover);
        SetBrush("FileTabs.ChromePressedBrush", chrome.Pressed);
        SetBrush("FileTabs.TabInactiveBrush", chrome.TabInactive);
        SetBrush("FileTabs.TabActiveBrush", chrome.TabActive);
        SetBrush("FileTabs.BookmarkFillBrush", chrome.Bookmark);
        SetBrush("FileTabs.BookmarkHoverBrush", chrome.BookmarkHover);
        SetBrush("FileTabs.DividerBrush", chrome.Divider);
        SetBrush("FileTabs.FieldFillBrush", chrome.Field);
        SetBrush("FileTabs.FieldHoverBrush", chrome.FieldHover);
        SetBrush("FileTabs.ChromeTextBrush", chrome.Text);
        SetBrush("FileTabs.ChromeMutedTextBrush", chrome.MutedText);
        SetBrush("FileTabs.SettingsCardBrush", chrome.SettingsCard);
        SetBrush("FileTabs.SettingsCardBorderBrush", chrome.SettingsCardBorder);
    }

    private sealed record ChromePalette(
        string Chrome, string Hover, string Pressed, string TabInactive, string TabActive,
        string Bookmark, string BookmarkHover, string Divider, string Field, string FieldHover,
        string Text, string MutedText, string SettingsCard, string SettingsCardBorder);

    private static void SetBrush(string key, string color)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
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
