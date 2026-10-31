using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Microsoft.WindowsAPICodePack.Shell;
// WinForms (UseWindowsForms) and WPF both define MessageBox; WPF wins here.
using MessageBox = System.Windows.MessageBox;
using Screen = System.Windows.Forms.Screen;

namespace FileTabs;

/// <summary>
/// Minimal settings dialog. Mutates AppSettings.Current in place and persists;
/// Cancel simply closes without touching anything.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// 打开设置时的自启状态。只有用户真的改动了这个复选框才写注册表；
    /// 否则“旧值（无参数）显示为已勾选 → 直接保存”会把它悄悄改写成
    /// --silent，等于用户没要求就改了自启行为。
    /// </summary>
    private readonly bool _startWithWindowsOnOpen;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FitToWorkArea();
        ThemeCombo.SelectedIndex = AppSettings.Current.Theme switch
        {
            nameof(AppTheme.Light) => 0,
            nameof(AppTheme.Dark) => 1,
            _ => 2,
        };
        NewTabPathBox.Text = AppSettings.Current.NewTabPath;
        _startWithWindowsOnOpen = StartupRegistration.IsEnabled();
        StartWithWindowsCheckBox.IsChecked = _startWithWindowsOnOpen;
        RestoreLastSessionCheckBox.IsChecked = AppSettings.Current.RestoreLastSession;
        ShowPreviewPaneCheckBox.IsChecked = AppSettings.Current.ShowPreviewPane;
        ShowNativeCommandBarCheckBox.IsChecked = AppSettings.Current.ShowNativeCommandBar;
        SearchSortByNameCheckBox.IsChecked = AppSettings.Current.SearchSortByName;
        DropPointEnabledCheckBox.IsChecked = AppSettings.Current.DropPointEnabled;
        CaptureExplorerCheckBox.IsChecked = AppSettings.Current.CaptureExplorer;
        RedirectFoldersCheckBox.IsChecked = NativeRedirect.IsInstalled();
        TerminalCombo.SelectedItem = TerminalCombo.Items
            .OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                AppSettings.Current.Terminal,
                StringComparison.OrdinalIgnoreCase));
        if (TerminalCombo.SelectedIndex < 0)
        {
            TerminalCombo.SelectedIndex = 0;
        }
        AboutVersionText.Text = "v" + (Assembly.GetExecutingAssembly()
            .GetName().Version is { } version
            ? version.ToString(3)
            : "?");
    }

    private void FitToWorkArea()
    {
        var screen = Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return;

        // WorkingArea is in physical pixels; WPF sizes are device-independent.
        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(
            new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transform.Transform(
            new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var availableHeight = bottomRight.Y - topLeft.Y;
        var availableWidth = bottomRight.X - topLeft.X;

        MaxHeight = Math.Max(240, availableHeight - 24);
        MaxWidth = Math.Max(320, availableWidth - 24);
        if (ActualHeight > MaxHeight)
        {
            Height = MaxHeight;
        }
        if (ActualWidth > MaxWidth)
        {
            Width = MaxWidth;
        }

        // CenterOwner can land off-screen when the owner is partly off-screen.
        var minLeft = topLeft.X + 12;
        var minTop = topLeft.Y + 12;
        var maxLeft = Math.Max(minLeft, bottomRight.X - ActualWidth - 12);
        var maxTop = Math.Max(minTop, bottomRight.Y - ActualHeight - 12);
        Left = Math.Clamp(Left, minLeft, maxLeft);
        Top = Math.Clamp(Top, minTop, maxTop);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
            Description = "选择新标签页默认打开的文件夹",
        };
        if (Directory.Exists(NewTabPathBox.Text))
        {
            dialog.SelectedPath = NewTabPathBox.Text;
        }
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            NewTabPathBox.Text = dialog.SelectedPath;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var input = NewTabPathBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            MessageBox.Show(this, "路径不能为空。", "FileTabs 设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _ = ShellObject.FromParsingName(input);
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法识别该路径，请输入合法的文件夹路径或 shell 位置。", "FileTabs 设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AppSettings.Current.NewTabPath = input;
        AppSettings.Current.ShowPreviewPane = ShowPreviewPaneCheckBox.IsChecked == true;
        AppSettings.Current.ShowNativeCommandBar = ShowNativeCommandBarCheckBox.IsChecked == true;
        AppSettings.Current.SearchSortByName = SearchSortByNameCheckBox.IsChecked == true;
        AppSettings.Current.CaptureExplorer = CaptureExplorerCheckBox.IsChecked == true;
        AppSettings.Current.DropPointEnabled = DropPointEnabledCheckBox.IsChecked == true;
        AppSettings.Current.RestoreLastSession = RestoreLastSessionCheckBox.IsChecked == true;
        AppSettings.Current.Terminal =
            (TerminalCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
            ?? nameof(TerminalKind.WindowsTerminal);
        var startWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        if (startWithWindows != _startWithWindowsOnOpen)
        {
            if (startWithWindows)
            {
                StartupRegistration.Enable();
            }
            else
            {
                StartupRegistration.Disable();
            }
        }
        var wantRedirect = RedirectFoldersCheckBox.IsChecked == true;
        if (wantRedirect && !NativeRedirect.IsInstalled())
        {
            NativeRedirect.Install();
        }
        else if (!wantRedirect && NativeRedirect.IsInstalled())
        {
            NativeRedirect.Uninstall();
        }
        AppSettings.Current.RedirectFolders = wantRedirect;
        var theme = ThemeCombo.SelectedIndex switch
        {
            0 => AppTheme.Light,
            1 => AppTheme.Dark,
            _ => AppTheme.FollowSystem,
        };
        AppSettings.Current.Theme = theme.ToString();
        ThemeManager.Apply(theme);
        AppStorage.SaveSettings(AppSettings.Current);
        (System.Windows.Application.Current as App)?.DropPoints.ApplySettings();
        DialogResult = true;
        Close();
    }
}
