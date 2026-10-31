using System.IO;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
// WinForms 与 WPF 都定义 UserControl，显式指定 WPF 版本。
using UserControl = System.Windows.Controls.UserControl;

namespace FileTabs;

/// <summary>
/// 目录模板功能页面：由旧的单功能 ExtensionsWindow 整体迁移而来，
/// 逻辑保持不变，只是宿主从 Window 变成了 UserControl。
/// 创建成功后把结果写到 owner.CreatedFolderPath 供 MainWindow 打开新标签。
/// </summary>
public partial class FolderTemplatePage : UserControl
{
    private readonly ExtensionsWindow _owner;
    private List<FolderTemplate> _templates = new();
    private bool _isInitializing = true;

    public FolderTemplatePage(ExtensionsWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_isInitializing)
            {
                RootPathBox.Text = _owner.InitialFolder ?? "";
                ReloadTemplates();
                _isInitializing = false;
                UpdateTemplateDescription();
                UpdatePreview();
                ItemNumberBox.Focus();
                ItemNumberBox.SelectAll();
            }
        };
    }

    private void ReloadTemplates(string? preferredName = null)
    {
        var wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            _templates = AppStorage.LoadFolderTemplates()
                .Where(template => !string.IsNullOrWhiteSpace(template.Name))
                .ToList();
            TemplateCombo.ItemsSource = _templates;

            if (_templates.Count == 0)
            {
                TemplateCombo.SelectedIndex = -1;
                TemplateCombo.IsEnabled = false;
                ItemNumberBox.IsEnabled = false;
                InputLabelText.Text = "名称/编号";
                ManageTemplatesButton.Content = "新建模板";
                TemplateDescriptionText.Text =
                    "还没有目录模板，点击左下角“新建模板”开始。";
                PreviewTree.ItemsSource = null;
                PreviewStatusText.Text = "";
                CreateButton.IsEnabled = false;
                return;
            }

            TemplateCombo.IsEnabled = true;
            ItemNumberBox.IsEnabled = true;
            ManageTemplatesButton.Content = "管理模板";
            var selected = _templates.FirstOrDefault(template =>
                               string.Equals(template.Name, preferredName,
                                   StringComparison.OrdinalIgnoreCase))
                           ?? _templates[0];
            TemplateCombo.SelectedItem = selected;
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        if (!_isInitializing)
        {
            UpdateTemplateDescription();
            UpdatePreview();
        }
    }

    private void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateTemplateDescription();
        UpdatePreview();
    }

    private void UpdateTemplateDescription()
    {
        var template = TemplateCombo.SelectedItem as FolderTemplate;
        TemplateDescriptionText.Text = template?.Description
            ?? (_templates.Count == 0
                ? "还没有目录模板，点击左下角“新建模板”开始。"
                : "");
        InputLabelText.Text = template is null
            ? "名称/编号"
            : FolderTemplateService.GetInputLabel(template);
        ItemNumberBox.ToolTip = $"输入{InputLabelText.Text}";
    }

    private void RootPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing) UpdatePreview();
    }

    private void ItemNumberBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing) UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_isInitializing) return;

        PreviewTree.ItemsSource = null;
        CreateButton.IsEnabled = false;
        CreateButton.Content = "创建文件夹";

        if (TemplateCombo.SelectedItem is not FolderTemplate template)
        {
            PreviewStatusText.Text = "请先选择模板。";
            return;
        }

        if (string.IsNullOrWhiteSpace(RootPathBox.Text))
        {
            PreviewStatusText.Text = "请选择保存位置。";
            return;
        }

        if (string.IsNullOrWhiteSpace(ItemNumberBox.Text))
        {
            PreviewStatusText.Text = $"输入{InputLabelText.Text}后显示预览。";
            return;
        }

        try
        {
            var plan = FolderTemplateService.CreatePlan(
                RootPathBox.Text, template, ItemNumberBox.Text);
            PreviewTree.ItemsSource = new[]
            {
                FolderTemplateService.BuildPreviewTree(plan),
            };
            PreviewStatusText.Text = plan.NewFolderCount == 0
                ? "全部目录已存在，可直接打开。"
                : $"将新建 {plan.NewFolderCount} 个目录，跳过 {plan.ExistingFolderCount} 个已存在目录。";
            CreateButton.Content = plan.NewFolderCount == 0
                ? "打开文件夹"
                : "创建文件夹";
            CreateButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            PreviewStatusText.Text = ex.Message;
        }
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
            Description = "选择目录的保存位置",
        };
        if (Directory.Exists(RootPathBox.Text))
        {
            dialog.SelectedPath = RootPathBox.Text;
        }
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            RootPathBox.Text = dialog.SelectedPath;
        }
    }

    private void ManageTemplates_Click(object sender, RoutedEventArgs e)
    {
        var selectedName = (TemplateCombo.SelectedItem as FolderTemplate)?.Name;
        var window = new TemplateManagerWindow(selectedName) { Owner = _owner };
        window.ShowDialog();
        ReloadTemplates(window.SelectedTemplateName ?? selectedName);
    }

    private void CreateFolders_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateCombo.SelectedItem is not FolderTemplate template) return;

        try
        {
            var plan = FolderTemplateService.CreatePlan(
                RootPathBox.Text, template, ItemNumberBox.Text);
            FolderTemplateService.CreateFolders(plan);
            _owner.CreatedFolderPath = plan.Folders[0].FullPath;
            _owner.DialogResult = true;
            _owner.Close();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(_owner, ex.Message, "创建文件夹失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdatePreview();
        }
    }
}
