using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
// WinForms 与 WPF 都定义这些类型，显式指定 WPF 版本。
using CheckBox = System.Windows.Controls.CheckBox;
using Cursors = System.Windows.Input.Cursors;
using UserControl = System.Windows.Controls.UserControl;

namespace FileTabs;

/// <summary>
/// 批量重命名页面：宿主打开扩展中心时把当前选中项传进来，
/// 页面只负责规则输入、预览和执行；目标名称的计算在 BatchRenameService 里。
/// </summary>
public partial class BatchRenamePage : UserControl
{
    private readonly ExtensionsWindow _owner;
    private readonly ObservableCollection<RenameRow> _rows = new();
    private RenamePlan? _plan;
    private bool _isInitializing = true;

    public BatchRenamePage(ExtensionsWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        PreviewList.ItemsSource = _rows;

        foreach (var path in owner.Context.SelectedPaths)
        {
            _rows.Add(new RenameRow
            {
                SourcePath = path,
                OldName = Path.GetFileName(path),
            });
        }

        SelectionSummaryText.Text = owner.Context.CurrentFolder is { } folder
            ? $"当前目录：{folder}　已选 {_rows.Count} 项"
            : $"已选 {_rows.Count} 项";

        Loaded += (_, _) =>
        {
            _isInitializing = false;
            UpdatePreview();
            RefreshUndoState();
        };
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        UpdatePreview();
    }

    private void RowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        // 事件先于绑定落地时也以勾选框当前状态为准，避免预览读到旧值。
        if (sender is CheckBox { DataContext: RenameRow row } check)
        {
            row.IsSelected = check.IsChecked == true;
        }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_isInitializing) return;

        var selected = _rows
            .Where(row => row.IsSelected)
            .Select(row => row.SourcePath)
            .ToList();

        if (selected.Count == 0)
        {
            _plan = null;
            foreach (var row in _rows)
            {
                row.NewName = "";
                row.Status = "未选中";
            }
            PreviewStatusText.Text = "请至少勾选一个项目。";
            RenameButton.IsEnabled = false;
            return;
        }

        if (!TryReadInt(StartNumberBox.Text, 0, 999999, out var startNumber))
        {
            _plan = null;
            PreviewStatusText.Text = "起始序号必须是 0 或正整数。";
            RenameButton.IsEnabled = false;
            return;
        }

        if (!TryReadInt(DigitsBox.Text, 1, 6, out var digits))
        {
            _plan = null;
            PreviewStatusText.Text = "位数必须在 1 到 6 之间。";
            RenameButton.IsEnabled = false;
            return;
        }

        _plan = BatchRenameService.CreatePlan(
            selected,
            TemplateBox.Text,
            startNumber,
            digits,
            PreserveExtensionCheck.IsChecked != false,
            DateTime.Now);

        var byPath = _plan.Items.ToDictionary(item => item.SourcePath, StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (!row.IsSelected)
            {
                row.NewName = "";
                row.Status = "未选中";
                continue;
            }

            if (!byPath.TryGetValue(row.SourcePath, out var item)) continue;
            row.OldName = item.OldName;
            row.NewName = item.NewName;
            row.Status = item.Error ?? (item.Changed ? "将重命名" : "无变化");
        }

        var errorCount = _plan.Items.Count(item => item.Error is not null);
        var unchangedCount = _plan.Items.Count(item => item.Error is null && !item.Changed);
        PreviewStatusText.Text = errorCount > 0
            ? $"有 {errorCount} 项无法执行；需先修正名称或取消勾选。"
            : unchangedCount > 0
                ? $"将重命名 {_plan.RenameCount} 项，{unchangedCount} 项无变化。"
                : $"将重命名 {_plan.RenameCount} 项。";

        RenameButton.IsEnabled = !_plan.HasErrors && _plan.RenameCount > 0;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _plan.HasErrors || _plan.RenameCount == 0) return;

        var history = new RenameHistory { At = DateTime.Now };
        var failures = new List<string>();
        var renamed = new Dictionary<string, (string NewPath, string NewName)>(StringComparer.Ordinal);

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            foreach (var item in _plan.Items.Where(item => item.Error is null && item.Changed))
            {
                try
                {
                    ShellFileOperations.Rename(item.SourcePath, item.NewName);
                    var newPath = Path.Combine(
                        Path.GetDirectoryName(item.SourcePath) ?? "", item.NewName);
                    history.Items.Add(new RenameHistoryItem
                    {
                        OldPath = item.SourcePath,
                        NewPath = newPath,
                    });
                    renamed[item.SourcePath] = (newPath, item.NewName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or COMException or OperationCanceledException or InvalidOperationException)
                {
                    failures.Add($"{item.OldName}：{ex.Message}");
                }
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (history.Items.Count > 0)
        {
            AppStorage.SaveRenameHistory(history);
            _owner.RefreshRequested = true;
        }

        // 把成功的行改指向新路径，后续还能基于当前状态继续套模板。
        var renamedPaths = renamed.Values
            .Select(result => result.NewPath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (!renamed.TryGetValue(row.SourcePath, out var result)) continue;
            row.SourcePath = result.NewPath;
            row.OldName = result.NewName;
            row.NewName = result.NewName;
        }

        RefreshUndoState();
        UpdatePreview();
        foreach (var row in _rows)
        {
            // UpdatePreview 会把名称没变化写成“无变化”，这里覆盖成本次执行结果。
            if (renamedPaths.Contains(row.SourcePath)) row.Status = "已重命名";
        }

        var message = $"已重命名 {history.Items.Count} 项。";
        if (failures.Count > 0)
        {
            message += $"\n\n失败 {failures.Count} 项：\n" +
                       string.Join("\n", failures.Take(10)) +
                       (failures.Count > 10 ? "\n…" : "");
        }
        MessageBox.Show(_owner, message, "批量重命名",
            MessageBoxButton.OK,
            failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var history = AppStorage.LoadRenameHistory();
        if (history is null || history.Items.Count == 0)
        {
            RefreshUndoState();
            return;
        }

        var remaining = new List<RenameHistoryItem>();
        var failures = new List<string>();
        var restored = new Dictionary<string, string>(StringComparer.Ordinal);

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // 逆序撤销，保证多条互换/链式改名时每一步的目标都是空闲的。
            for (var index = history.Items.Count - 1; index >= 0; index--)
            {
                var item = history.Items[index];
                var currentExists = File.Exists(item.NewPath) || Directory.Exists(item.NewPath);
                if (!currentExists)
                {
                    failures.Add($"{Path.GetFileName(item.NewPath)}：文件已不存在");
                    remaining.Add(item);
                    continue;
                }

                var oldExists = File.Exists(item.OldPath) || Directory.Exists(item.OldPath);
                // 只改大小写时 OldPath 指向的仍是同一个文件，不算占用。
                if (oldExists &&
                    !string.Equals(item.OldPath, item.NewPath, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{Path.GetFileName(item.NewPath)}：原名称已被占用");
                    remaining.Add(item);
                    continue;
                }

                try
                {
                    ShellFileOperations.Rename(item.NewPath, Path.GetFileName(item.OldPath));
                    restored[item.NewPath] = item.OldPath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or COMException or OperationCanceledException or InvalidOperationException)
                {
                    failures.Add($"{Path.GetFileName(item.NewPath)}：{ex.Message}");
                    remaining.Add(item);
                }
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (remaining.Count == 0)
        {
            AppStorage.ClearRenameHistory();
        }
        else
        {
            AppStorage.SaveRenameHistory(new RenameHistory
            {
                At = history.At,
                Items = remaining,
            });
        }

        foreach (var row in _rows)
        {
            if (!restored.TryGetValue(row.SourcePath, out var oldPath)) continue;
            row.SourcePath = oldPath;
            row.OldName = Path.GetFileName(oldPath);
            row.NewName = Path.GetFileName(oldPath);
        }

        _owner.RefreshRequested = true;
        RefreshUndoState();
        UpdatePreview();

        var message = $"已撤销 {restored.Count} 项。";
        if (failures.Count > 0)
        {
            message += $"\n\n未能撤销 {failures.Count} 项：\n" +
                       string.Join("\n", failures.Take(10)) +
                       (failures.Count > 10 ? "\n…" : "");
        }
        MessageBox.Show(_owner, message, "撤销重命名",
            MessageBoxButton.OK,
            failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void RefreshUndoState()
    {
        var history = AppStorage.LoadRenameHistory();
        UndoButton.IsEnabled = history is { Items.Count: > 0 };
        UndoButton.ToolTip = history is { Items.Count: > 0 }
            ? $"撤销 {history.At:yyyy-MM-dd HH:mm} 的批量重命名（{history.Items.Count} 项）"
            : "没有可撤销的批量重命名";
    }

    private static bool TryReadInt(string text, int min, int max, out int value)
        => int.TryParse(text.Trim(), out value) && value >= min && value <= max;

    /// <summary>列表行模型。勾选状态与预览结果都通过绑定刷新。</summary>
    private sealed class RenameRow : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private string _oldName = "";
        private string _newName = "";
        private string _status = "";

        public string SourcePath { get; set; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public string OldName
        {
            get => _oldName;
            set => SetField(ref _oldName, value);
        }

        public string NewName
        {
            get => _newName;
            set => SetField(ref _newName, value);
        }

        public string Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
