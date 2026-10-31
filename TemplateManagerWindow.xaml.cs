using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace FileTabs;

public partial class TemplateManagerWindow : Window
{
    private List<FolderTemplate> _templates = new();
    private FolderTemplate? _editingOriginal;
    private FolderTemplate? _selectedTemplate;
    private TextBox? _variableTarget;
    private bool _isLoading;
    private bool _isDirty;

    public string? SelectedTemplateName { get; private set; }

    public TemplateManagerWindow(string? preferredName)
    {
        InitializeComponent();
        Closing += TemplateManagerWindow_Closing;
        LoadTemplates(preferredName);
        Loaded += (_, _) => NameBox.Focus();
    }

    private void LoadTemplates(string? preferredName)
    {
        _templates = AppStorage.LoadFolderTemplates()
            .Where(template => !string.IsNullOrWhiteSpace(template.Name))
            .ToList();

        _isLoading = true;
        TemplateList.ItemsSource = _templates;
        var selected = _templates.FirstOrDefault(template =>
                           string.Equals(template.Name, preferredName,
                               StringComparison.OrdinalIgnoreCase))
                       ?? _templates.FirstOrDefault();
        TemplateList.SelectedItem = selected;
        LoadEditor(selected ?? new FolderTemplate(), selected);
        _isLoading = false;
    }

    private void LoadEditor(FolderTemplate template, FolderTemplate? original)
    {
        _editingOriginal = original;
        _selectedTemplate = original;
        SelectedTemplateName = original?.Name;
        NameBox.Text = template.Name;
        InputLabelBox.Text = FolderTemplateService.GetInputLabel(template);
        DescriptionBox.Text = template.Description;
        RootFolderBox.Text = template.RootFolder;
        FoldersBox.Text = string.Join(Environment.NewLine, template.Folders ?? new List<string>());
        _isDirty = false;
        _variableTarget = FoldersBox;
        UpdateButtons();
        UpdateStatus();
        UpdatePreview();
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || TemplateList.SelectedItem is not FolderTemplate selected ||
            ReferenceEquals(selected, _selectedTemplate))
        {
            return;
        }

        if (!ConfirmDiscardChanges())
        {
            _isLoading = true;
            TemplateList.SelectedItem = _selectedTemplate;
            _isLoading = false;
            return;
        }

        _isLoading = true;
        LoadEditor(selected, selected);
        _isLoading = false;
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;

        _isLoading = true;
        TemplateList.SelectedItem = null;
        LoadEditor(new FolderTemplate(), null);
        _isLoading = false;
        NameBox.Focus();
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_editingOriginal is null || !ConfirmDiscardChanges()) return;

        var answer = MessageBox.Show(this,
            $"确定删除模板“{_editingOriginal.Name}”吗？",
            "删除模板", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var candidate = _templates
            .Where(template => !ReferenceEquals(template, _editingOriginal))
            .ToList();
        if (!AppStorage.SaveFolderTemplates(candidate))
        {
            MessageBox.Show(this, "模板保存失败，请检查数据目录权限。", "删除失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _templates = candidate;
        var next = _templates.FirstOrDefault();
        _isLoading = true;
        TemplateList.ItemsSource = null;
        TemplateList.ItemsSource = _templates;
        TemplateList.SelectedItem = next;
        LoadEditor(next ?? new FolderTemplate(), next);
        _isLoading = false;
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        var draft = ReadDraft();
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            MessageBox.Show(this, "请输入模板名称。", "无法保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }
        if (_templates.Any(template => !ReferenceEquals(template, _editingOriginal) &&
                                       string.Equals(template.Name, draft.Name,
                                           StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "模板名称不能重复。", "无法保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        try
        {
            // Reuse the real planner so save-time validation matches creation.
            _ = FolderTemplateService.CreatePlan(Path.GetTempPath(), draft, "示例");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "模板格式有误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var candidate = _templates.Select(CloneTemplate).ToList();
        if (_editingOriginal is null)
        {
            candidate.Add(draft);
        }
        else
        {
            var index = _templates.IndexOf(_editingOriginal);
            candidate[index] = draft;
        }

        if (!AppStorage.SaveFolderTemplates(candidate))
        {
            MessageBox.Show(this, "模板保存失败，请检查数据目录权限。", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _templates = candidate;
        _editingOriginal = draft;
        _selectedTemplate = draft;
        SelectedTemplateName = draft.Name;
        _isLoading = true;
        TemplateList.ItemsSource = null;
        TemplateList.ItemsSource = _templates;
        TemplateList.SelectedItem = draft;
        _isLoading = false;
        _isDirty = false;
        UpdateButtons();
        UpdateStatus();
        TemplateStatusText.Text = "已保存";
    }

    private void EditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading) return;
        _isDirty = true;
        UpdateStatus();
        UpdatePreview();
    }

    private void VariableTarget_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox) _variableTarget = textBox;
    }

    private void InsertVariable_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string variable) return;
        var target = _variableTarget ?? FoldersBox;
        var index = target.CaretIndex;
        target.Text = target.Text.Insert(index, variable);
        target.CaretIndex = index + variable.Length;
        target.Focus();
    }

    private void UpdatePreview()
    {
        PreviewTree.ItemsSource = null;
        var draft = ReadDraft();
        if (string.IsNullOrWhiteSpace(draft.RootFolder))
        {
            PreviewStatusText.Text = "填写根目录规则后显示预览。";
            return;
        }

        try
        {
            var plan = FolderTemplateService.CreatePlan(
                Path.GetTempPath(), draft, "示例");
            PreviewTree.ItemsSource = new[]
            {
                FolderTemplateService.BuildPreviewTree(plan),
            };
            PreviewStatusText.Text = $"示例：{plan.NewFolderCount} 个目录";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            PreviewStatusText.Text = ex.Message;
        }
    }

    private void UpdateButtons()
    {
        DeleteTemplateButton.IsEnabled = _editingOriginal is not null;
    }

    private void UpdateStatus()
    {
        TemplateStatusText.Text = _isDirty
            ? "有未保存修改"
            : _editingOriginal is null ? "新建模板" : "已保存";
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty) return true;
        return MessageBox.Show(this,
            "当前模板有未保存修改，继续将丢失这些修改。",
            "未保存的修改", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void TemplateManagerWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges()) e.Cancel = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private FolderTemplate ReadDraft() =>
        new()
        {
            Name = NameBox.Text.Trim(),
            InputLabel = string.IsNullOrWhiteSpace(InputLabelBox.Text)
                ? "名称/编号"
                : InputLabelBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
            RootFolder = RootFolderBox.Text.Trim(),
            Folders = FoldersBox.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList(),
        };

    private static FolderTemplate CloneTemplate(FolderTemplate template) =>
        new()
        {
            Name = template.Name,
            InputLabel = template.InputLabel,
            Description = template.Description,
            RootFolder = template.RootFolder,
            Folders = template.Folders?.ToList() ?? new List<string>(),
        };
}
