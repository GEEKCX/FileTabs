using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FileTabs;

public sealed class FolderTemplate
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string InputLabel { get; set; } = "";
    public string RootFolder { get; set; } = "{名称}";
    public List<string> Folders { get; set; } = new();
}

public sealed class FolderTemplatePlanItem
{
    public IReadOnlyList<string> Segments { get; init; } = Array.Empty<string>();
    public string FullPath { get; init; } = "";
    public bool Exists { get; init; }
}

public sealed class FolderTemplatePlan
{
    public string BaseFolder { get; init; } = "";
    public IReadOnlyList<FolderTemplatePlanItem> Folders { get; init; } =
        Array.Empty<FolderTemplatePlanItem>();

    public int NewFolderCount => Folders.Count(item => !item.Exists);
    public int ExistingFolderCount => Folders.Count(item => item.Exists);
}

/// <summary>批量创建的一组计划；每行输入对应一棵 Plans 里的树。</summary>
public sealed class FolderTemplatePlanSet
{
    public string BaseFolder { get; init; } = "";
    public IReadOnlyList<FolderTemplatePlan> Plans { get; init; } =
        Array.Empty<FolderTemplatePlan>();

    public int NewFolderCount => Plans.Sum(plan => plan.NewFolderCount);
    public int ExistingFolderCount => Plans.Sum(plan => plan.ExistingFolderCount);
}

public sealed class FolderPreviewNode
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public IReadOnlyList<FolderPreviewNode> Children { get; init; } =
        Array.Empty<FolderPreviewNode>();
}

/// <summary>
/// Builds and creates folder trees from user-editable JSON templates.
/// Paths are always relative to the selected base folder; templates cannot
/// escape it with an absolute path or "..".
/// </summary>
public static class FolderTemplateService
{
    private static readonly Regex VariableToken = new(@"\{[^{}]+\}", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        },
        StringComparer.OrdinalIgnoreCase);

    public static FolderTemplatePlan CreatePlan(
        string baseFolder,
        FolderTemplate template,
        string inputValue,
        DateTime? now = null)
    {
        return CreatePlans(baseFolder, template, inputValue, now).Plans[0];
    }

    /// <summary>
    /// 批量创建：输入按行（或逗号/分号）拆分，每行生成一棵独立的目录树。
    /// {序号} 渲染为该行在输入中的三位序号（001、002…）；单行输入时序号为 001。
    /// 相同渲染结果的行自动合并，不会重复累积。
    /// </summary>
    public static FolderTemplatePlanSet CreatePlans(
        string baseFolder,
        FolderTemplate template,
        string inputValue,
        DateTime? now = null)
    {
        if (template is null) throw new ArgumentException("请选择一个目录模板。");

        var baseFull = NormalizeBaseFolder(baseFolder);
        var inputLabel = GetInputLabel(template);
        if (string.IsNullOrWhiteSpace(inputValue))
        {
            throw new ArgumentException($"请输入{inputLabel}。");
        }

        // 每行一个实例；逗号/分号也算分隔，方便从表格里整列粘贴。
        var entries = inputValue
            .Split(new[] { '\r', '\n', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(raw => raw.Trim())
            .Where(raw => raw.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
        {
            throw new ArgumentException($"请输入{inputLabel}。");
        }

        var plans = new List<FolderTemplatePlan>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            plans.Add(BuildSinglePlan(baseFull, template, entries[i], i + 1, now));
        }
        return new FolderTemplatePlanSet { BaseFolder = baseFull, Plans = plans };
    }

    private static FolderTemplatePlan BuildSinglePlan(
        string baseFull,
        FolderTemplate template,
        string input,
        int sequence,
        DateTime? now)
    {
        var inputLabel = GetInputLabel(template);
        ValidateSegment(input, inputLabel);

        if (string.IsNullOrWhiteSpace(template.RootFolder))
        {
            throw new ArgumentException("模板根目录不能为空。");
        }
        var renderedRoot = RenderValue(template.RootFolder, input, sequence, now ?? DateTime.Now);
        ValidateSegment(renderedRoot, "模板根目录");

        var planItems = new List<FolderTemplatePlanItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPlanItem(planItems, seenPaths, baseFull, new[] { renderedRoot });

        foreach (var folder in template.Folders ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                throw new ArgumentException("模板包含空的目录路径。");
            }

            var rendered = RenderValue(folder, input, sequence, now ?? DateTime.Now);
            var relativeSegments = SplitRelativePath(rendered);
            var segments = new List<string> { renderedRoot };
            segments.AddRange(relativeSegments);
            AddPlanItem(planItems, seenPaths, baseFull, segments);
        }

        return new FolderTemplatePlan
        {
            BaseFolder = baseFull,
            Folders = planItems,
        };
    }

    public static string GetInputLabel(FolderTemplate template)
    {
        if (!string.IsNullOrWhiteSpace(template.InputLabel))
        {
            return template.InputLabel.Trim();
        }

        // Keep templates created before InputLabel existed readable: their
        // {货号} placeholder should still be presented as "货号".
        var usesLegacyVariable = template.RootFolder.Contains("{货号}",
            StringComparison.Ordinal) ||
            (template.Folders ?? new List<string>()).Any(folder =>
                folder.Contains("{货号}", StringComparison.Ordinal));
        return usesLegacyVariable ? "货号" : "名称/编号";
    }

    public static int CreateFolders(FolderTemplatePlan plan)
    {
        if (!Directory.Exists(plan.BaseFolder))
        {
            throw new DirectoryNotFoundException($"根目录不存在：{plan.BaseFolder}");
        }

        // Check every parent first so a file in the path cannot leave a
        // half-created tree behind.
        foreach (var item in plan.Folders)
        {
            var current = plan.BaseFolder;
            foreach (var segment in item.Segments)
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current))
                {
                    throw new IOException($"目标路径中已有同名文件：{current}");
                }
            }
        }

        var created = 0;
        foreach (var item in plan.Folders)
        {
            if (Directory.Exists(item.FullPath)) continue;
            Directory.CreateDirectory(item.FullPath);
            created++;
        }
        return created;
    }

    public static FolderPreviewNode BuildPreviewTree(FolderTemplatePlan plan)
    {
        return BuildPreviewTrees(new[] { plan })[0];
    }

    /// <summary>批量预览：所有实例合并到同一个保存位置根节点下。</summary>
    public static FolderPreviewNode[] BuildPreviewTrees(IReadOnlyList<FolderTemplatePlan> plans)
    {
        var root = new MutablePreviewNode(plans[0].BaseFolder, isBase: true);
        foreach (var plan in plans)
        {
            AddPlanToPreview(root, plan);
        }
        return new[] { root.ToPreviewNode() };
    }

    private static void AddPlanToPreview(MutablePreviewNode root, FolderTemplatePlan plan)
    {
        foreach (var item in plan.Folders)
        {
            var parent = root;
            var currentPath = plan.BaseFolder;
            foreach (var segment in item.Segments)
            {
                currentPath = Path.Combine(currentPath, segment);
                if (!parent.Children.TryGetValue(segment, out var child))
                {
                    child = new MutablePreviewNode(currentPath)
                    {
                        Exists = Directory.Exists(currentPath),
                    };
                    parent.Children.Add(segment, child);
                }
                parent = child;
            }
            parent.Exists = item.Exists;
        }
    }

    private static void AddPlanItem(
        ICollection<FolderTemplatePlanItem> items,
        ISet<string> seenPaths,
        string baseFolder,
        IEnumerable<string> segments)
    {
        var segmentList = segments.ToList();
        var fullPath = baseFolder;
        foreach (var segment in segmentList)
        {
            fullPath = Path.Combine(fullPath, segment);
        }
        fullPath = Path.GetFullPath(fullPath);
        var trimmedBase = Path.TrimEndingDirectorySeparator(baseFolder);
        var basePrefix = trimmedBase.EndsWith(Path.DirectorySeparatorChar)
            ? trimmedBase
            : trimmedBase + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("模板目录超出了所选根目录。");
        }
        if (!seenPaths.Add(fullPath))
        {
            throw new ArgumentException($"模板包含重复目录：{fullPath}");
        }

        items.Add(new FolderTemplatePlanItem
        {
            Segments = segmentList,
            FullPath = fullPath,
            Exists = Directory.Exists(fullPath),
        });
    }

    private static string NormalizeBaseFolder(string baseFolder)
    {
        if (string.IsNullOrWhiteSpace(baseFolder))
        {
            throw new ArgumentException("请选择保存位置。");
        }

        var fullPath = Path.GetFullPath(baseFolder.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"保存位置不存在：{fullPath}");
        }
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string RenderValue(string value, string inputValue, int sequence, DateTime now)
    {
        var rendered = value
            .Replace("{名称}", inputValue, StringComparison.Ordinal)
            .Replace("{货号}", inputValue, StringComparison.Ordinal)
            .Replace("{序号}", sequence.ToString("000", CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("{日期}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("{月份}", now.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        var unknown = VariableToken.Match(rendered);
        if (unknown.Success)
        {
            throw new ArgumentException($"模板包含不支持的变量：{unknown.Value}");
        }
        return rendered.Trim();
    }

    private static IReadOnlyList<string> SplitRelativePath(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains(':'))
        {
            throw new ArgumentException($"模板目录必须是相对路径：{path}");
        }

        var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.None);
        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"模板目录不能包含空层级：{path}");
        }
        foreach (var part in parts)
        {
            ValidateSegment(part, "模板目录");
        }
        return parts;
    }

    private static void ValidateSegment(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.EndsWith(' ') || value.EndsWith('.') ||
            Path.IsPathRooted(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"{fieldName}不是有效的文件夹名称：{value}");
        }

        var stem = Path.GetFileNameWithoutExtension(value);
        if (ReservedNames.Contains(stem))
        {
            throw new ArgumentException($"{fieldName}不能使用 Windows 保留名称：{value}");
        }
    }

    private sealed class MutablePreviewNode
    {
        public MutablePreviewNode(string fullPath, bool isBase = false)
        {
            FullPath = fullPath;
            IsBase = isBase;
        }

        public string FullPath { get; }
        public bool IsBase { get; }
        public bool Exists { get; set; }
        public Dictionary<string, MutablePreviewNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public FolderPreviewNode ToPreviewNode() =>
            new()
            {
                Name = IsBase ? FullPath : Path.GetFileName(FullPath),
                Status = IsBase ? "" : Exists ? "已存在" : "新建",
                Children = Children.Values
                    .Select(child => child.ToPreviewNode())
                    .ToList(),
            };
    }
}
