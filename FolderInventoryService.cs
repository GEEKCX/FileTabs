using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FileTabs;

public enum InventoryFormat
{
    Csv,
    Markdown,
    Json,
}

public sealed record InventoryOptions(
    bool Recursive,
    bool IncludeFiles,
    bool IncludeFolders,
    bool IncludeHidden);

public sealed record InventoryEntry(
    string RelativePath,
    string Name,
    bool IsDirectory,
    long Size,
    DateTime ModifiedAt,
    DateTime CreatedAt,
    string Extension);

public sealed record InventoryScanResult(
    string Root,
    DateTime ScannedAt,
    IReadOnlyList<InventoryEntry> Entries,
    int FileCount,
    int FolderCount,
    long TotalSize,
    int SkippedCount);

/// <summary>
/// 目录清单的扫描与序列化。扫描不跟随目录联接（reparse point），
/// 无法读取的子目录计入 SkippedCount 后跳过，不让整次导出失败。
/// </summary>
public static class FolderInventoryService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static InventoryScanResult Scan(
        string root,
        InventoryOptions options,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        var rootDirectory = new DirectoryInfo(root);
        if (!rootDirectory.Exists)
        {
            throw new DirectoryNotFoundException($"目录不存在：{root}");
        }

        var entries = new List<InventoryEntry>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootDirectory);

        var fileCount = 0;
        var folderCount = 0;
        var skippedCount = 0;
        long totalSize = 0;
        var scannedCount = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            FileSystemInfo[] items;
            try
            {
                items = directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                skippedCount++;
                continue;
            }

            foreach (var info in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedCount++;
                if (scannedCount % 200 == 0) progress?.Report(scannedCount);

                FileAttributes attributes;
                try
                {
                    attributes = info.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skippedCount++;
                    continue;
                }

                var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (!options.IncludeHidden &&
                    (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    continue;
                }

                string relativePath;
                try
                {
                    relativePath = Path.GetRelativePath(root, info.FullName);
                }
                catch (ArgumentException)
                {
                    skippedCount++;
                    continue;
                }

                if (info is DirectoryInfo childDirectory)
                {
                    folderCount++;
                    if (options.IncludeFolders)
                    {
                        entries.Add(new InventoryEntry(
                            relativePath, info.Name, true, 0,
                            SafeLastWriteTime(info), SafeCreationTime(info),
                            ""));
                    }

                    // 联接/符号链接可以指向父目录，递归进去会无限循环。
                    if (options.Recursive && !isReparsePoint)
                    {
                        pending.Push(childDirectory);
                    }
                }
                else if (info is FileInfo file)
                {
                    long size;
                    try
                    {
                        size = file.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        size = 0;
                        skippedCount++;
                    }

                    fileCount++;
                    totalSize += size;
                    if (options.IncludeFiles)
                    {
                        entries.Add(new InventoryEntry(
                            relativePath, file.Name, false, size,
                            SafeLastWriteTime(file), SafeCreationTime(file),
                            file.Extension));
                    }
                }
            }
        }

        progress?.Report(scannedCount);
        entries.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

        return new InventoryScanResult(
            root, DateTime.Now, entries, fileCount, folderCount, totalSize, skippedCount);
    }

    public static string Format(InventoryScanResult result, InventoryFormat format)
        => format switch
        {
            InventoryFormat.Csv => ToCsv(result),
            InventoryFormat.Markdown => ToMarkdown(result),
            InventoryFormat.Json => ToJson(result),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public static Encoding GetEncoding(InventoryFormat format)
        // Excel 只在有 BOM 时才能稳定识别 UTF-8 中文。
        => format == InventoryFormat.Csv ? Utf8Bom : Utf8NoBom;

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024d;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string ToCsv(InventoryScanResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("相对路径,类型,大小(字节),修改时间,创建时间");
        foreach (var entry in result.Entries)
        {
            builder
                .Append(CsvField(entry.RelativePath)).Append(',')
                .Append(entry.IsDirectory ? "文件夹" : "文件").Append(',')
                .Append(entry.Size.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .AppendLine();
        }
        return builder.ToString();
    }

    private static string ToMarkdown(InventoryScanResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# 目录清单：{Path.GetFileName(result.Root.TrimEnd('\\', '/'))}");
        builder.AppendLine();
        builder.AppendLine($"- 目录：`{result.Root}`");
        builder.AppendLine($"- 生成时间：{result.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(
            $"- 文件 {result.FileCount} 个，文件夹 {result.FolderCount} 个，总计 {FormatSize(result.TotalSize)}");
        if (result.SkippedCount > 0)
        {
            builder.AppendLine($"- 跳过 {result.SkippedCount} 项（无权限或无法读取）");
        }
        builder.AppendLine();
        builder.AppendLine("| 相对路径 | 类型 | 大小 | 修改时间 |");
        builder.AppendLine("|---|---|---:|---|");
        foreach (var entry in result.Entries)
        {
            builder.Append("| ")
                .Append(MarkdownField(entry.RelativePath)).Append(" | ")
                .Append(entry.IsDirectory ? "文件夹" : "文件").Append(" | ")
                .Append(entry.IsDirectory ? "—" : FormatSize(entry.Size)).Append(" | ")
                .Append(entry.ModifiedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .AppendLine(" |");
        }
        return builder.ToString();
    }

    private static string ToJson(InventoryScanResult result)
    {
        var payload = new
        {
            root = result.Root,
            scannedAt = result.ScannedAt,
            fileCount = result.FileCount,
            folderCount = result.FolderCount,
            totalSize = result.TotalSize,
            skippedCount = result.SkippedCount,
            entries = result.Entries,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string MarkdownField(string value)
        => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    private static DateTime SafeLastWriteTime(FileSystemInfo info)
    {
        try { return info.LastWriteTime; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static DateTime SafeCreationTime(FileSystemInfo info)
    {
        try { return info.CreationTime; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
