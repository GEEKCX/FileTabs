using System.Globalization;
using System.IO;

namespace FileTabs;

/// <summary>单个待重命名项的预览结果。Error 非空表示该项不能执行。</summary>
public sealed record RenamePreviewItem(
    string SourcePath,
    string OldName,
    string NewName,
    bool Changed,
    string? Error);

/// <summary>一次批量重命名的完整预览。</summary>
public sealed record RenamePlan(IReadOnlyList<RenamePreviewItem> Items)
{
    public bool HasErrors => Items.Any(item => item.Error is not null);

    /// <summary>真正会改名（名称有变化且无错误）的项数。</summary>
    public int RenameCount => Items.Count(item => item.Error is null && item.Changed);
}

/// <summary>
/// 批量重命名的纯规划逻辑：只计算目标名称并做冲突校验，不碰文件系统写入。
/// 变量沿用目录模板的中文风格：{原名}、{原文件名}、{扩展名}、{序号}、{日期}。
/// </summary>
public static class BatchRenameService
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 生成重命名计划。序号只按传入顺序递增，校验失败的项目也占位，
    /// 这样预览里的编号与实际替换结果一致，不会因为某一项报错而跳号。
    /// </summary>
    public static RenamePlan CreatePlan(
        IReadOnlyList<string> paths,
        string template,
        int startNumber,
        int digits,
        bool preserveExtension,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return new RenamePlan(paths
                .Select(path => new RenamePreviewItem(
                    path, Path.GetFileName(path), "", false, "请输入命名模板"))
                .ToList());
        }

        digits = Math.Clamp(digits, 1, 6);
        startNumber = Math.Max(startNumber, 0);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var preliminary = new List<RenamePreviewItem>(paths.Count);

        for (var index = 0; index < paths.Count; index++)
        {
            preliminary.Add(BuildItem(paths[index], template, startNumber + index,
                digits, preserveExtension, date));
        }

        // 同一批里两个源改成同一个名字时，两项都不能执行。
        var duplicateTargets = preliminary
            .Where(item => item.Error is null && item.Changed)
            .Select(item => Path.Combine(
                Path.GetDirectoryName(item.SourcePath) ?? "", item.NewName))
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (duplicateTargets.Count == 0) return new RenamePlan(preliminary);

        return new RenamePlan(preliminary
            .Select(item =>
            {
                if (item.Error is not null || !item.Changed) return item;
                var target = Path.Combine(
                    Path.GetDirectoryName(item.SourcePath) ?? "", item.NewName);
                return duplicateTargets.Contains(target)
                    ? item with { Error = "与其他项目的新名称重复" }
                    : item;
            })
            .ToList());
    }

    private static RenamePreviewItem BuildItem(
        string sourcePath, string template, int sequence, int digits,
        bool preserveExtension, string date)
    {
        var oldName = Path.GetFileName(sourcePath);
        if (string.IsNullOrEmpty(oldName))
        {
            return new RenamePreviewItem(sourcePath, oldName, "", false, "不是有效的文件或目录");
        }

        bool isDirectory;
        try
        {
            // 文件与目录都可能存在；两者都不存在才是选择期间被移走了。
            isDirectory = Directory.Exists(sourcePath);
            if (!isDirectory && !File.Exists(sourcePath))
            {
                return new RenamePreviewItem(sourcePath, oldName, "", false, "项目已不存在");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RenamePreviewItem(sourcePath, oldName, "", false,
                $"无法读取项目：{ex.Message}");
        }

        var originalBaseName = isDirectory ? oldName : Path.GetFileNameWithoutExtension(oldName);
        var extension = isDirectory ? "" : Path.GetExtension(oldName);

        var newName = template
            .Replace("{原文件名}", oldName, StringComparison.Ordinal)
            .Replace("{原名}", originalBaseName, StringComparison.Ordinal)
            // {扩展名} 不含点，模板里通常自己写点；保留扩展名时才自动补。
            .Replace("{扩展名}", extension.TrimStart('.'), StringComparison.Ordinal)
            .Replace("{序号}", sequence.ToString($"D{digits}", CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("{日期}", date, StringComparison.Ordinal);

        // 模板没显式处理扩展名时默认保留，避免批量改名把文件类型改丢。
        if (!isDirectory && preserveExtension &&
            !template.Contains("{原文件名}", StringComparison.Ordinal) &&
            !template.Contains("{扩展名}", StringComparison.Ordinal))
        {
            newName += extension;
        }

        var error = ValidateName(newName);
        if (error is not null)
        {
            return new RenamePreviewItem(sourcePath, oldName, newName, false, error);
        }

        var changed = !string.Equals(newName, oldName, StringComparison.Ordinal);
        if (!changed)
        {
            return new RenamePreviewItem(sourcePath, oldName, newName, false, null);
        }

        var directory = Path.GetDirectoryName(sourcePath) ?? "";
        var targetPath = Path.Combine(directory, newName);
        // 仅大小写变化时目标就是源文件本身，不算冲突。
        if (!string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(targetPath) || Directory.Exists(targetPath)))
        {
            return new RenamePreviewItem(sourcePath, oldName, newName, true, "目标名称已存在");
        }

        return new RenamePreviewItem(sourcePath, oldName, newName, true, null);
    }

    private static string? ValidateName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return "新名称为空";
        if (newName.Length > 255) return "新名称超过 255 个字符";
        if (newName.EndsWith(' ') || newName.EndsWith('.'))
            return "名称不能以空格或点结尾";

        var invalid = Path.GetInvalidFileNameChars();
        if (newName.IndexOfAny(invalid) >= 0) return "名称包含非法字符";

        // Windows reserves the device name regardless of the extension:
        // CON.txt and CON.foo.bar are both invalid.
        var stem = newName.Split('.', 2)[0];
        if (ReservedNames.Contains(stem)) return "名称是 Windows 保留名称";
        return null;
    }
}
