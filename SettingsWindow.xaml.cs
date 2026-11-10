using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Microsoft.WindowsAPICodePack.Shell;
// WinForms (UseWindowsForms) and WPF both define MessageBox/Application; WPF wins here.
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
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
        DataDirectoryText.Text = AppStorage.DataDirectory;
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

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (AppStorage.TryOpenDataDirectory()) return;
        MessageBox.Show(this,
            $"无法打开数据目录：{Environment.NewLine}{AppStorage.DataDirectory}",
            "FileTabs 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!AppStorage.HasLatestBackups())
        {
            MessageBox.Show(this, "没有找到可用的备份。", "恢复最近备份",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Stop the background session/history timers before the confirmation
        // message box: its nested message loop could otherwise save the
        // current session and rotate the .bak we are about to restore.
        var main = Owner as MainWindow;
        main?.PrepareForDataRestore();

        var confirm = MessageBox.Show(this,
            "将用每个数据文件的上一版（.bak）覆盖当前内容。\n" +
            "包括会话、书签、工作区、目录模板、重命名历史、设置和最近位置。\n\n" +
            "当前内容会被替换，恢复后 FileTabs 会自动重启。继续吗？",
            "恢复最近备份", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            main?.ResumeDataSavingAfterRestoreCancel();
            return;
        }

        var restored = AppStorage.RestoreLatestBackups();
        if (restored == 0)
        {
            main?.ResumeDataSavingAfterRestoreCancel();
            MessageBox.Show(this, "恢复失败，详情见数据目录中的 app.log。", "恢复最近备份",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(this,
            $"已恢复 {restored} 个数据文件。FileTabs 将重启以加载恢复的数据。",
            "恢复最近备份", MessageBoxButton.OK, MessageBoxImage.Information);

        if (Application.Current is App app)
        {
            // PrepareForDataRestore already suppressed the normal close-time
            // save; this restarts so the restored files are actually loaded.
            app.RestartForDataRestore();
        }
        else
        {
            Application.Current?.Shutdown();
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
