using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
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
/// Swaps the Fluent.* color resources between light and dark palettes.
/// The XAML resource dictionary keeps a single set of keys; only the brush
/// values are replaced so every DynamicResource reference updates instantly.
/// </summary>
public static class ThemeManager
{
    private static readonly (string Key, string Light, string Dark)[] Palette =
    {
        ("Fluent.Background", "#FFF3F3F3", "#FF202020"),
        ("Fluent.Surface", "#FFFFFFFF", "#FF2B2B2B"),
        ("Fluent.Border", "#19000000", "#33FFFFFF"),
        ("Fluent.Text", "#FF1B1B1B", "#FFF2F2F2"),
        ("Fluent.TextSecondary", "#FF6F6F6F", "#FFA0A0A0"),
        ("Fluent.Accent", "#FF0067C0", "#FF4CC2FF"),
        ("Fluent.AccentHover", "#FF1A74C7", "#FF63CEFF"),
        ("Fluent.AccentPressed", "#FF005BA1", "#FF3AAEE4"),
        ("Fluent.Hover", "#0F000000", "#1AFFFFFF"),
        ("Fluent.Checked", "#16000000", "#22FFFFFF"),
        ("Fluent.Danger", "#FFE81123", "#FFFF5A6E"),
    };

    public static void Apply(AppTheme theme)
    {
        bool dark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => IsSystemDark(),
        };

        // Sync WPF UI's theme (FluentWindow chrome, backdrop, control brushes)
        // alongside our own Fluent.* palette so both stay in lockstep.
        ApplicationThemeManager.Apply(
            dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.Mica);

        var dict = Application.Current.Resources;
        foreach (var (key, light, darkValue) in Palette)
        {
            dict[key] = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    dark ? darkValue : light));
        }
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

    /// <summary>
    /// Make WinForms content (the ExplorerBrowser control host) blend into the
    /// themed chrome while shell paints its own file list background.
    /// </summary>
    public static System.Drawing.Color ShellHostBackColor
        => Application.Current.Resources["Fluent.Background"] is SolidColorBrush brush
            ? System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B)
            : System.Drawing.Color.White;
}
