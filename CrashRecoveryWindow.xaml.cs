using System.Windows;
// WinForms 与 WPF 都定义 MessageBox，显式使用 WPF 版本。
using MessageBox = System.Windows.MessageBox;

namespace FileTabs;

public enum CrashRecoveryAction
{
    Exit,
    Restart,
}

/// <summary>
/// Modal fallback shown after an unhandled UI-thread exception. It stays open
/// while the user inspects the data directory, so the final choice is still
/// restart or exit; the app does not resume normal work after this dialog.
/// </summary>
public partial class CrashRecoveryWindow : Window
{
    public CrashRecoveryAction Action { get; private set; } = CrashRecoveryAction.Exit;

    public CrashRecoveryWindow(Exception exception)
    {
        InitializeComponent();
        ErrorText.Text = $"{exception.GetType().Name}: {exception.Message}";
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        Action = CrashRecoveryAction.Restart;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Action = CrashRecoveryAction.Exit;
        DialogResult = false;
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (AppStorage.TryOpenDataDirectory()) return;
        MessageBox.Show(this,
            $"无法打开数据目录：{Environment.NewLine}{AppStorage.DataDirectory}",
            "FileTabs", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
