using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
// WinForms 与 WPF 都定义这些类型，显式指定 WPF 版本。
using CheckBox = System.Windows.Controls.CheckBox;
using UserControl = System.Windows.Controls.UserControl;

namespace FileTabs;

/// <summary>
/// 目录清理页面：一次扫描得到空文件夹、最大的 N 个文件和超期文件，
/// 删除统一走回收站；删除后重新扫描，让新变空的上级目录也能出现。
/// </summary>
public partial class DirectoryCleanupPage : UserControl
{
    private const int StaleLimit = 1000;

    private readonly ExtensionsWindow _owner;
    private readonly ObservableCollection<CleanupRow> _emptyRows = new();
    private readonly ObservableCollection<CleanupRow> _largestRows = new();
    private readonly ObservableCollection<CleanupRow> _staleRows = new();
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;
    private bool _hasLoaded;

    public DirectoryCleanupPage(ExtensionsWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        EmptyList.ItemsSource = _emptyRows;
        LargestList.ItemsSource = _largestRows;
        StaleList.ItemsSource = _staleRows;

        if (owner.Context.CurrentFolder is { } folder)
        {
            FolderText.Text = $"当前目录：{folder}";
        }
        else
        {
            FolderText.Text = "当前标签不在普通目录中，无法扫描。";
            ScanButton.IsEnabled = false;
        }

        Loaded += async (_, _) =>
        {
            // 页面在功能切换时会被重新挂载，只自动扫描第一次。
            if (_hasLoaded) return;
            _hasLoaded = true;
            await ScanAsync();
        };

        // 关掉扩展中心时停止后台扫描，避免窗口已关闭还继续扫盘。
        owner.Closed += (_, _) => _cancellation?.Cancel();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_isBusy || _owner.Context.CurrentFolder is not { } root) return;

        _cancellation = new CancellationTokenSource();
        SetBusy(true);

        var options = new CleanupOptions(
            Recursive: RecursiveCheck.IsChecked == true,
            IncludeHidden: IncludeHiddenCheck.IsChecked == true,
            LargestCount: ParseInt(LargestCountBox.Text, 50, 1, 500),
            StaleDays: ParseInt(StaleDaysBox.Text, 90, 1, 36500),
            StaleLimit: StaleLimit);

        try
        {
            var progress = new Progress<int>(scanned =>
                ScanStatusText.Text = $"已扫描 {scanned:N0} 项…");
            var result = await Task.Run(
                () => DirectoryCleanupService.Scan(root, options, _cancellation.Token, progress),
                _cancellation.Token);
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "已取消扫描。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            ScanStatusText.Text = "扫描失败。";
            MessageBox.Show(_owner, ex.Message, "目录清理扫描失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void ShowResult(CleanupResult result)
    {
        Fill(_emptyRows, result.EmptyFolders, showDashForSize: true);
        Fill(_largestRows, result.LargestFiles, showDashForSize: false);
        Fill(_staleRows, result.StaleFiles, showDashForSize: false);

        EmptyTab.Header = $"空文件夹 ({_emptyRows.Count:N0})";
        LargestTab.Header = $"大文件 ({_largestRows.Count:N0})";
        StaleTab.Header = result.StaleFileCount > _staleRows.Count
            ? $"超期文件 ({_staleRows.Count:N0} / {result.StaleFileCount:N0})"
            : $"超期文件 ({_staleRows.Count:N0})";

        ScanStatusText.Text =
            $"共 {result.FileCount:N0} 个文件、{result.FolderCount:N0} 个文件夹，" +
            $"总计 {FolderInventoryService.FormatSize(result.TotalSize)}" +
            (result.SkippedCount > 0 ? $"，跳过 {result.SkippedCount:N0} 项" : "") +
            $"；空文件夹 {_emptyRows.Count:N0} 个，超期文件 {result.StaleFileCount:N0} 个" +
            $"（{FolderInventoryService.FormatSize(result.StaleTotalSize)}）。";

        UpdateDeleteState();
    }

    private static void Fill(
        ObservableCollection<CleanupRow> target,
        IReadOnlyList<CleanupEntry> entries,
        bool showDashForSize)
    {
        target.Clear();
        foreach (var entry in entries)
        {
            target.Add(new CleanupRow
            {
                FullPath = entry.Path,
                Path = entry.RelativePath,
                SizeText = showDashForSize
                    ? "—"
                    : FolderInventoryService.FormatSize(entry.Size),
                ModifiedText = entry.ModifiedAt == DateTime.MinValue
                    ? ""
                    : entry.ModifiedAt.ToString("yyyy-MM-dd HH:mm"),
            });
        }
    }

    private void RowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CleanupRow row } check)
        {
            row.IsSelected = check.IsChecked == true;
        }
        UpdateDeleteState();
    }

    private void CleanupTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDeleteState();

    private void ToggleSelect_Click(object sender, RoutedEventArgs e)
    {
        var rows = CurrentRows();
        if (rows.Count == 0) return;
        var selectAll = !rows.All(row => row.IsSelected);
        foreach (var row in rows) row.IsSelected = selectAll;
        UpdateDeleteState();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        ScanStatusText.Text = "正在取消…";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var rows = CurrentRows().Where(row => row.IsSelected).ToList();
        if (rows.Count == 0) return;

        var preview = string.Join("\n", rows.Take(8).Select(row => row.Path));
        if (rows.Count > 8) preview += "\n…";
        var confirm = MessageBox.Show(_owner,
            $"将把 {rows.Count} 项移入回收站：\n\n{preview}",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            ShellFileOperations.DeleteToRecycleBin(
                rows.Select(row => row.FullPath).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or COMException or OperationCanceledException or InvalidOperationException)
        {
            MessageBox.Show(_owner, ex.Message, "删除失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _owner.RefreshRequested = true;
        await ScanAsync();
    }

    private ObservableCollection<CleanupRow> CurrentRows()
        => ReferenceEquals(CleanupTabs.SelectedItem, EmptyTab) ? _emptyRows
            : ReferenceEquals(CleanupTabs.SelectedItem, LargestTab) ? _largestRows
            : _staleRows;

    private void UpdateDeleteState()
    {
        DeleteButton.IsEnabled = !_isBusy && CurrentRows().Any(row => row.IsSelected);
        ToggleSelectButton.IsEnabled = !_isBusy && CurrentRows().Count > 0;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        ScanButton.IsEnabled = !busy && _owner.Context.CurrentFolder is not null;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RecursiveCheck.IsEnabled = !busy;
        IncludeHiddenCheck.IsEnabled = !busy;
        LargestCountBox.IsEnabled = !busy;
        StaleDaysBox.IsEnabled = !busy;
        DeleteButton.IsEnabled = false;
        ToggleSelectButton.IsEnabled = !busy;
        if (!busy) UpdateDeleteState();
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text.Trim(), out var value) && value >= min && value <= max
            ? value
            : fallback;

    private sealed class CleanupRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string FullPath { get; init; } = "";
        public string Path { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string ModifiedText { get; init; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
