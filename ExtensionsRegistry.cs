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
    }
}
