using System.IO;

namespace FileTabs;

public sealed record CleanupOptions(
    bool Recursive,
    bool IncludeHidden,
    int LargestCount,
    int StaleDays,
    int StaleLimit);

public sealed record CleanupEntry(
    string Path,
    string RelativePath,
    long Size,
    DateTime ModifiedAt);

public sealed record CleanupResult(
    string Root,
    DateTime ScannedAt,
    IReadOnlyList<CleanupEntry> EmptyFolders,
    IReadOnlyList<CleanupEntry> LargestFiles,
    IReadOnlyList<CleanupEntry> StaleFiles,
    int FileCount,
    int FolderCount,
    long TotalSize,
    int SkippedCount,
    int StaleFileCount,
    long StaleTotalSize);

/// <summary>
/// 目录清理的一次性扫描：空文件夹、最大的 N 个文件、超过指定天数的旧文件。
/// 与目录清单不同，这里为了准确判断“空”，始终读取隐藏/系统条目；
/// 隐藏开关只影响“是否把它们列成可删除项”。
/// </summary>
public static class DirectoryCleanupService
{
    public static CleanupResult Scan(
        string root,
        CleanupOptions options,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        var rootDirectory = new DirectoryInfo(root);
        if (!rootDirectory.Exists)
        {
            throw new DirectoryNotFoundException($"目录不存在：{root}");
        }

        var largestCount = Math.Clamp(options.LargestCount, 1, 500);
        var staleDays = Math.Clamp(options.StaleDays, 1, 36500);
        var staleLimit = Math.Clamp(options.StaleLimit, 1, 10000);
        var cutoff = DateTime.Now.AddDays(-staleDays);

        var emptyFolders = new List<CleanupEntry>();
        var largestFiles = new List<CleanupEntry>();
        var staleFiles = new List<CleanupEntry>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootDirectory);

        var fileCount = 0;
        var folderCount = 0;
        var skippedCount = 0;
        var staleFileCount = 0;
        long totalSize = 0;
        long staleTotalSize = 0;
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
                // 读不到就不建议删这里的上级目录：里面可能还有东西。
                skippedCount++;
                continue;
            }

            if (items.Length == 0 && !PathsEqual(directory.FullName, root))
            {
                var attributes = SafeAttributes(directory);
                if (options.IncludeHidden || !IsHiddenOrSystem(attributes))
                {
                    emptyFolders.Add(new CleanupEntry(
                        directory.FullName,
                        Path.GetRelativePath(root, directory.FullName),
                        0,
                        SafeLastWriteTime(directory)));
                }
            }

            foreach (var info in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedCount++;
                if (scannedCount % 500 == 0) progress?.Report(scannedCount);

                var attributes = SafeAttributes(info);
                var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                var isHidden = IsHiddenOrSystem(attributes);

                if (info is DirectoryInfo childDirectory)
                {
                    folderCount++;
                    if (options.Recursive && !isReparsePoint)
                    {
                        pending.Push(childDirectory);
                    }
                    else if (!options.Recursive && !isReparsePoint)
                    {
                        // 不递归时子目录不会被弹出，但顶层空目录仍要能报出来。
                        AddIfEmpty(emptyFolders, childDirectory, root,
                            options.IncludeHidden, ref skippedCount);
                    }
                    continue;
                }

                if (info is not FileInfo file) continue;

                fileCount++;
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
                totalSize += size;

                var modified = SafeLastWriteTime(file);

                // 隐藏项和联接不可选，也不会出现在超期/大文件列表里，
                // 所以不计入可清理的超期数量和大小，避免“统计 4 个、列表只有 3 个”。
                if (isReparsePoint || (isHidden && !options.IncludeHidden)) continue;

                var entry = new CleanupEntry(
                    file.FullName,
                    Path.GetRelativePath(root, file.FullName),
                    size,
                    modified);

                if (modified != DateTime.MinValue && modified < cutoff)
                {
                    staleFileCount++;
                    staleTotalSize += size;
                    if (staleFiles.Count < staleLimit)
                    {
                        staleFiles.Add(entry);
                    }
                }

                largestFiles.Add(entry);
                // 只保留最大的若干项，避免全盘扫描时把每个文件都留在内存里。
                if (largestFiles.Count > largestCount * 4)
                {
                    largestFiles.Sort((left, right) => right.Size.CompareTo(left.Size));
                    largestFiles.RemoveRange(largestCount, largestFiles.Count - largestCount);
                }
            }
        }

        progress?.Report(scannedCount);
        largestFiles.Sort((left, right) => right.Size.CompareTo(left.Size));
        if (largestFiles.Count > largestCount)
        {
            largestFiles.RemoveRange(largestCount, largestFiles.Count - largestCount);
        }
        staleFiles.Sort((left, right) => left.ModifiedAt.CompareTo(right.ModifiedAt));
        emptyFolders.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

        return new CleanupResult(
            root, DateTime.Now, emptyFolders, largestFiles, staleFiles,
            fileCount, folderCount, totalSize, skippedCount,
            staleFileCount, staleTotalSize);
    }

    private static void AddIfEmpty(
        List<CleanupEntry> emptyFolders,
        DirectoryInfo directory,
        string root,
        bool includeHidden,
        ref int skippedCount)
    {
        FileSystemInfo[] children;
        try
        {
            children = directory.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            skippedCount++;
            return;
        }

        if (children.Length != 0) return;
        var attributes = SafeAttributes(directory);
        if (!includeHidden && IsHiddenOrSystem(attributes)) return;
        emptyFolders.Add(new CleanupEntry(
            directory.FullName,
            Path.GetRelativePath(root, directory.FullName),
            0,
            SafeLastWriteTime(directory)));
    }

    private static bool IsHiddenOrSystem(FileAttributes attributes)
        => (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private static FileAttributes SafeAttributes(FileSystemInfo info)
    {
        try { return info.Attributes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileAttributes.Normal;
        }
    }

    private static DateTime SafeLastWriteTime(FileSystemInfo info)
    {
        try { return info.LastWriteTime; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
