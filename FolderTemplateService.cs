using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
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


/// <summary>从现有目录反向生成的扫描结果。</summary>
public sealed class FolderScanResult
{
    public List<string> RelativePaths { get; init; } = new();
    public int TotalFound { get; init; }
    public int SkippedDepth { get; init; }
    public int SkippedLimit { get; init; }
    public bool Truncated => SkippedDepth > 0 || SkippedLimit > 0;
}

/// <summary>
/// Builds and creates folder trees from user-editable JSON templates.
/// Paths are always relative to the selected base folder; templates cannot
/// escape it with an absolute path or "..".
/// </summary>
public static class FolderTemplateService
{

    #region Sprint 3：模板导入/导出与从目录反向生成

    /// <summary>反向生成时跳过的常见垃圾/工具目录（不区分大小写）。</summary>
    private static readonly HashSet<string> SkipDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", ".svn", ".hg",
        "dist", "build", "packages", ".idea", ".gradle", "vendor",
        "__pycache__", ".pytest_cache", "coverage", ".next", ".nuxt",
        "target", ".venv", "venv", ".tox", "site-packages",
    };

    /// <summary>反向生成的默认深度与数量上限。</summary>
    public const int ScanMaxDepth = 3;
    public const int ScanMaxNodes = 200;

    /// <summary>
    /// 从现有目录反向生成模板路径列表：BFS 遍历目录树，产出相对路径（一行一条，
    /// 与模板编辑器的"一行=一条相对路径"模型一致）。空目录也纳入——模板语义
    /// 是"建这棵树"，某层存在本身就有意义。不跟随符号链接/挂载点
    /// （ReparsePoint 属性让枚举器直接跳过）。
    /// </summary>
    public static FolderScanResult ScanFolderTree(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            throw new ArgumentException("请选择要扫描的文件夹。");
        }
        var rootFull = Path.GetFullPath(rootFolder.Trim());
        if (!Directory.Exists(rootFull))
        {
            throw new DirectoryNotFoundException($"文件夹不存在：{rootFull}");
        }

        // AttributesToSkip ReparsePoint：枚举器不进入符号链接/ junction。
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.ReparsePoint,
        };

        var paths = new List<string>();
        var queue = new Queue<(string FullPath, string RelativePath, int Depth)>();
        queue.Enqueue((Path.TrimEndingDirectorySeparator(rootFull), "", 0));
        var totalFound = 0;
        var skippedDepth = 0;
        var skippedLimit = 0;

        while (queue.Count > 0)
        {
            var (dirPath, relPath, depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dirPath).EnumerateFileSystemInfos("*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // 单个目录读不了就跳过，不中断整棵树。
            }

            foreach (var entry in entries)
            {
                if (entry is not DirectoryInfo dir) continue;
                totalFound++;

                if (SkipDirectoryNames.Contains(dir.Name))
                {
                    continue; // 垃圾/工具目录整棵剪掉，连同其子目录。
                }

                var childRel = string.IsNullOrEmpty(relPath)
                    ? dir.Name
                    : relPath + Path.DirectorySeparatorChar + dir.Name;

                if (depth >= ScanMaxDepth)
                {
                    skippedDepth++;
                    continue;
                }
                if (paths.Count >= ScanMaxNodes)
                {
                    skippedLimit++;
                    continue;
                }

                paths.Add(childRel);
                queue.Enqueue((dir.FullName, childRel, depth + 1));
            }
        }

        return new FolderScanResult
        {
            RelativePaths = paths,
            TotalFound = totalFound,
            SkippedDepth = skippedDepth,
            SkippedLimit = skippedLimit,
        };
    }

    /// <summary>
    /// 反向生成扫描结果的人类可读摘要，含截断提示；无截断时返回 null。
    /// </summary>
    public static string? DescribeScanTruncation(FolderScanResult result)
    {
        if (result is null || !result.Truncated) return null;
        var parts = new List<string>();
        if (result.SkippedDepth > 0)
        {
            parts.Add($"{result.SkippedDepth} 个子目录因超过 {ScanMaxDepth} 层深度被跳过");
        }
        if (result.SkippedLimit > 0)
        {
            parts.Add($"达到 {ScanMaxNodes} 个目录上限后停止，剩余 {result.SkippedLimit} 个未纳入");
        }
        return "扫描结果已截断：" + string.Join("；", parts) + "。";
    }

    /// <summary>
    /// 解析模板 JSON 文件，兼容单个对象与数组两种结构。返回的列表里
    /// Name 为空的条目视为无效，由调用方决定报错措辞。
    /// </summary>
    public static List<FolderTemplate> ParseTemplateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("文件内容为空。");
        }

        json = json.TrimStart('\uFEFF'); // 容忍带 BOM 的 UTF-8 文件
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // 统一契约：所有坏输入都抛 ArgumentException，调用方（导入对话框）
            // 只需要 catch 一种类型即可给出友好提示，不会让 JsonException 漏出去。
            throw new ArgumentException($"文件不是有效的 JSON：{ex.Message}", ex);
        }
        using (document)
        {
            var root = document.RootElement;

            List<FolderTemplate> templates;
            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    templates = JsonSerializer.Deserialize<List<FolderTemplate>>(
                        root.GetRawText(), JsonSerializeOptions) ?? new List<FolderTemplate>();
                    break;
                case JsonValueKind.Object:
                    var single = JsonSerializer.Deserialize<FolderTemplate>(
                        root.GetRawText(), JsonSerializeOptions);
                    templates = single is null
                        ? new List<FolderTemplate>()
                        : new List<FolderTemplate> { single };
                    break;
                default:
                    throw new ArgumentException("文件不是有效的模板 JSON（应为对象或数组）。");
            }

            if (templates.Count == 0)
            {
                throw new ArgumentException("文件中没有模板数据。");
            }
            if (templates.Any(template => string.IsNullOrWhiteSpace(template.Name)))
            {
                throw new ArgumentException("文件中存在缺少名称的模板。");
            }
            foreach (var template in templates)
            {
                template.Folders ??= new List<string>();
            }
            return templates;
        }
    }

    /// <summary>导入重名时使用的后缀。</summary>
    public const string ImportSuffix = " (导入)";

    /// <summary>给重名模板生成唯一名称：追加" (导入)"，仍冲突则加序号。</summary>
    public static string MakeUniqueImportName(string name, ISet<string> takenNames)
    {
        var candidate = name + ImportSuffix;
        var index = 2;
        while (takenNames.Contains(candidate))
        {
            candidate = $"{name}{ImportSuffix}{index}";
            index++;
        }
        return candidate;
    }

    /// <summary>导出序列化选项：与 folder-templates.json 保持一致（缩进、中文直出）。</summary>
    private static readonly JsonSerializerOptions JsonSerializeOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把模板列表序列化为导出 JSON（与存储文件同一格式）。</summary>
    public static string SerializeTemplates(IEnumerable<FolderTemplate> templates) =>
        JsonSerializer.Serialize(templates, JsonSerializeOptions);

    #endregion
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
