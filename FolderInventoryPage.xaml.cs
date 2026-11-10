using System.IO;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
// WinForms 与 WPF 都定义 UserControl，显式指定 WPF 版本。
using UserControl = System.Windows.Controls.UserControl;

namespace FileTabs;

/// <summary>
/// 目录清单页面：把当前目录（可选递归）导出成 CSV / Markdown / JSON。
/// 扫描与写文件都在后台线程，页面只负责选项、进度和取消。
/// </summary>
public partial class FolderInventoryPage : UserControl
{
    private const int PreviewLimit = 1000;

    private readonly ExtensionsWindow _owner;
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;

    public FolderInventoryPage(ExtensionsWindow owner)
    {
        _owner = owner;
        InitializeComponent();

        if (owner.Context.CurrentFolder is { } folder)
        {
            FolderText.Text = $"当前目录：{folder}";
        }
        else
        {
            FolderText.Text = "当前标签不在普通目录中，无法导出清单。";
            ExportButton.IsEnabled = false;
        }

        // 关掉扩展中心时停止后台扫描，避免窗口已关闭还继续扫盘。
        owner.Closed += (_, _) => _cancellation?.Cancel();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _owner.Context.CurrentFolder is not { } root) return;

        var format = SelectedFormat();
        var extension = format switch
        {
            InventoryFormat.Markdown => "md",
            InventoryFormat.Json => "json",
            _ => "csv",
        };
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出目录清单",
            Filter = format switch
            {
                InventoryFormat.Markdown => "Markdown 文件 (*.md)|*.md",
                InventoryFormat.Json => "JSON 文件 (*.json)|*.json",
                _ => "CSV 文件 (*.csv)|*.csv",
            },
            DefaultExt = extension,
            AddExtension = true,
            FileName = DefaultFileName(root, extension),
        };
        if (dialog.ShowDialog(_owner) != true) return;

        var options = new InventoryOptions(
            Recursive: RecursiveCheck.IsChecked == true,
            IncludeFiles: IncludeFilesCheck.IsChecked == true,
            IncludeFolders: IncludeFoldersCheck.IsChecked == true,
            IncludeHidden: IncludeHiddenCheck.IsChecked == true);

        await RunExportAsync(root, dialog.FileName, options, format);
    }

    private async Task RunExportAsync(
        string root, string destinationPath, InventoryOptions options, InventoryFormat format)
    {
        _cancellation = new CancellationTokenSource();
        SetBusy(true);

        try
        {
            var progress = new Progress<int>(scanned =>
                ScanStatusText.Text = $"已扫描 {scanned:N0} 项…");

            var result = await Task.Run(() =>
            {
                var scan = FolderInventoryService.Scan(
                    root, options, _cancellation.Token, progress);
                var text = FolderInventoryService.Format(scan, format);
                File.WriteAllText(destinationPath, text,
                    FolderInventoryService.GetEncoding(format));
                return scan;
            }, _cancellation.Token);

            ShowPreview(result);
            ScanStatusText.Text =
                $"已导出 {result.FileCount:N0} 个文件、{result.FolderCount:N0} 个文件夹，" +
                $"共 {FolderInventoryService.FormatSize(result.TotalSize)}" +
                (result.SkippedCount > 0 ? $"，跳过 {result.SkippedCount:N0} 项" : "") +
                (result.Entries.Count > PreviewLimit
                    ? $"；预览仅显示前 {PreviewLimit:N0} 项"
                    : "") + "。";
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "已取消扫描，未写入文件。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            ScanStatusText.Text = "导出失败。";
            MessageBox.Show(_owner, ex.Message, "导出目录清单失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void ShowPreview(InventoryScanResult result)
    {
        var rows = result.Entries
            .Take(PreviewLimit)
            .Select(entry => new PreviewRow(
                entry.RelativePath,
                entry.IsDirectory ? "—" : FolderInventoryService.FormatSize(entry.Size),
                entry.ModifiedAt == DateTime.MinValue
                    ? ""
                    : entry.ModifiedAt.ToString("yyyy-MM-dd HH:mm")))
            .ToList();
        PreviewList.ItemsSource = rows;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        ScanStatusText.Text = "正在取消…";
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        ExportButton.IsEnabled = !busy && _owner.Context.CurrentFolder is not null;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        FormatCombo.IsEnabled = !busy;
        RecursiveCheck.IsEnabled = !busy;
        IncludeFoldersCheck.IsEnabled = !busy;
        IncludeFilesCheck.IsEnabled = !busy;
        IncludeHiddenCheck.IsEnabled = !busy;
        if (busy)
        {
            PreviewList.ItemsSource = null;
            ScanStatusText.Text = "准备扫描…";
        }
    }

    private InventoryFormat SelectedFormat()
    {
        if (FormatCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            return tag switch
            {
                "md" => InventoryFormat.Markdown,
                "json" => InventoryFormat.Json,
                _ => InventoryFormat.Csv,
            };
        }
        return InventoryFormat.Csv;
    }

    private static string DefaultFileName(string root, string extension)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "目录";
        var invalid = Path.GetInvalidFileNameChars();
        folderName = string.Concat(folderName.Where(character => !invalid.Contains(character)));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "目录";
        return $"清单-{folderName}-{DateTime.Now:yyyyMMdd-HHmm}.{extension}";
    }

    private sealed record PreviewRow(string Path, string SizeText, string ModifiedText);
}
