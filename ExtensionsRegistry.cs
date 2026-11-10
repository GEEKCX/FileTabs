using System.Windows;
using Wpf.Ui.Controls;

namespace FileTabs;

/// <summary>
/// 扩展中心里展示的一个功能条目。
/// Id 用于页面缓存与去重；CreateContent 惰性创建功能页面。
/// </summary>
public sealed record ExtensionFeature(
    string Id,
    string Title,
    string Description,
    SymbolRegular Icon,
    Func<ExtensionsWindow, FrameworkElement> CreateContent);

/// <summary>
/// 打开扩展中心时的宿主快照。功能页面只依赖这份数据，
/// 不再各自向 ExtensionsWindow 加专用属性。
/// </summary>
public sealed class ExtensionsContext
{
    /// <summary>当前标签所在目录；特殊位置（此电脑等）为 null。</summary>
    public string? CurrentFolder { get; init; }

    /// <summary>文件列表里当前选中的项（完整路径，按 shell 选择顺序）。</summary>
    public IReadOnlyList<string> SelectedPaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 扩展功能注册表：新增功能时在静态构造里追加一个 Register 调用，
/// 提供一个 UserControl 即可出现在扩展中心左侧导航，不引入插件加载系统。
/// </summary>
public static class ExtensionsRegistry
{
    private static readonly List<ExtensionFeature> Features = new();

    /// <summary>注册一个扩展功能；Id 重复直接抛异常，便于开发期发现冲突。</summary>
    public static void Register(ExtensionFeature feature)
    {
        if (Features.Any(existing => existing.Id == feature.Id))
        {
            throw new InvalidOperationException($"扩展功能 Id 重复：{feature.Id}");
        }
        Features.Add(feature);
    }

    /// <summary>获取全部已注册功能（按注册顺序）。</summary>
    public static IReadOnlyList<ExtensionFeature> Snapshot() => Features.ToList();

    static ExtensionsRegistry()
    {
        // 注册项一：目录模板（由旧的单功能扩展窗口整体迁移而来）。
        Register(new ExtensionFeature(
            "folder-template",
            "目录模板",
            "把重复的文件操作保存成可复用模板。",
            SymbolRegular.Folder24,
            owner => new FolderTemplatePage(owner)));

        // 注册项二：批量重命名（使用宿主传入的当前选中项）。
        Register(new ExtensionFeature(
            "batch-rename",
            "批量重命名",
            "按模板批量改名，执行前预览冲突。",
            SymbolRegular.Rename24,
            owner => new BatchRenamePage(owner)));

        // 注册项三：把当前目录导出成清单文件。
        Register(new ExtensionFeature(
            "folder-inventory",
            "目录清单",
            "导出目录内容为 CSV、Markdown 或 JSON。",
            SymbolRegular.DocumentTable24,
            owner => new FolderInventoryPage(owner)));

        // 注册项四：扫描空目录、大文件和超期文件。
        Register(new ExtensionFeature(
            "folder-cleanup",
            "目录清理",
            "找出空文件夹、大文件和长期未改动的文件。",
            SymbolRegular.Broom24,
            owner => new DirectoryCleanupPage(owner)));
    }
}
