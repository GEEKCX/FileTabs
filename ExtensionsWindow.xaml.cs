using System.Windows;
using System.Windows.Controls;

namespace FileTabs;

/// <summary>
/// 扩展中心窗口外壳：左侧列出 ExtensionsRegistry 注册的功能，
/// 右侧用 ContentControl 承载选中功能的页面（UserControl）。
/// 窗口本身不实现任何具体功能；页面通过 Page 关联回传结果，
/// 例如目录模板页面会把创建结果写到 Owner 的 CreatedFolderPath。
/// </summary>
public partial class ExtensionsWindow : Window
{
    /// <summary>由扩展页面写入、供宿主窗口（MainWindow）读取的结果值。</summary>
    public string? CreatedFolderPath { get; internal set; }

    /// <summary>打开窗口时所在的目录，作为目录模板功能的默认保存位置。</summary>
    public string? InitialFolder { get; }

    /// <summary>功能 Id → 已创建页面，切换功能时保留各自状态。</summary>
    private readonly Dictionary<string, FrameworkElement> _pages = new();

    public ExtensionsWindow(string? initialFolder)
    {
        InitialFolder = initialFolder;
        InitializeComponent();
        Loaded += (_, _) => InitializeFeatures();
    }

    private void InitializeFeatures()
    {
        var features = ExtensionsRegistry.Snapshot();
        FeatureList.ItemsSource = features;
        if (features.Count > 0)
        {
            FeatureList.SelectedIndex = 0;
        }
    }

    private void FeatureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FeatureList.SelectedItem is not ExtensionFeature feature) return;

        // 惰性创建 + 缓存：同一功能切回来时保留输入状态。
        if (!_pages.TryGetValue(feature.Id, out var page))
        {
            page = feature.CreateContent(this);
            _pages[feature.Id] = page;
        }
        FeatureHost.Content = page;
    }
}
