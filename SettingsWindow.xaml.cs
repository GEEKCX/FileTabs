using System.IO;
using System.Windows;
using Microsoft.WindowsAPICodePack.Shell;
// WinForms (UseWindowsForms) and WPF both define MessageBox; WPF wins here.
using MessageBox = System.Windows.MessageBox;

namespace FileTabs;

/// <summary>
/// Minimal settings dialog. Mutates AppSettings.Current in place and persists;
/// Cancel simply closes without touching anything.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        ThemeCombo.SelectedIndex = AppSettings.Current.Theme switch
        {
            nameof(AppTheme.Light) => 0,
            nameof(AppTheme.Dark) => 1,
            _ => 2,
        };
        NewTabPathBox.Text = AppSettings.Current.NewTabPath;
        ShowBookmarkBarCheckBox.IsChecked = AppSettings.Current.ShowBookmarkBar;
        ShowPreviewPaneCheckBox.IsChecked = AppSettings.Current.ShowPreviewPane;
        SearchSortByNameCheckBox.IsChecked = AppSettings.Current.SearchSortByName;
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
        AppSettings.Current.ShowBookmarkBar = ShowBookmarkBarCheckBox.IsChecked == true;
        AppSettings.Current.ShowPreviewPane = ShowPreviewPaneCheckBox.IsChecked == true;
        AppSettings.Current.SearchSortByName = SearchSortByNameCheckBox.IsChecked == true;
        var theme = ThemeCombo.SelectedIndex switch
        {
            0 => AppTheme.Light,
            1 => AppTheme.Dark,
            _ => AppTheme.FollowSystem,
        };
        AppSettings.Current.Theme = theme.ToString();
        ThemeManager.Apply(theme);
        AppStorage.SaveSettings(AppSettings.Current);
        DialogResult = true;
        Close();
    }
}
