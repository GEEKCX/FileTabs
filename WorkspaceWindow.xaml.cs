using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
// WinForms (UseWindowsForms) and WPF both define MessageBox; WPF wins here.
using MessageBox = System.Windows.MessageBox;

namespace FileTabs;

public sealed record WorkspaceSummary(string Name, int TabCount, DateTime LastUsedAt)
{
    public string Summary => $"{TabCount} 个标签 · {LastUsedAt:yyyy-MM-dd HH:mm}";
}

public partial class WorkspaceWindow : Window
{
    private readonly ObservableCollection<WorkspaceSummary> _items = new();

    public WorkspaceWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        foreach (var w in AppStorage.LoadWorkspaces()
                     .OrderByDescending(w => w.LastUsedAt))
        {
            _items.Add(new WorkspaceSummary(w.Name, w.Tabs.Count, w.LastUsedAt));
        }
        WorkspaceList.ItemsSource = _items;
        if (_items.Count > 0) WorkspaceList.SelectedIndex = 0;
        WorkspaceList.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && WorkspaceList.SelectedItem is WorkspaceSummary item)
            {
                Delete(item);
                e.Handled = true;
            }
        };
    }

    public string? SelectedWorkspaceName =>
        (WorkspaceList.SelectedItem as WorkspaceSummary)?.Name;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkspaceName is null)
        {
            MessageBox.Show(this, "请先选择一个工作区。", "FileTabs",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceSummary item }) Delete(item);
    }

    private void Delete(WorkspaceSummary item)
    {
        var confirm = MessageBox.Show(this, $"删除工作区「{item.Name}」？", "FileTabs",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        var workspaces = AppStorage.LoadWorkspaces();
        workspaces.RemoveAll(w => string.Equals(w.Name, item.Name, StringComparison.Ordinal));
        AppStorage.SaveWorkspaces(workspaces);
        _items.Remove(item);
    }
}
