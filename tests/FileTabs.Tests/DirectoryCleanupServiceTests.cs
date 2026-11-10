using System.IO;
using FileTabs;

namespace FileTabs.Tests;

public sealed class DirectoryCleanupServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"filetabs-cleanup-{Guid.NewGuid():N}");

    private readonly string _oldFile;
    private readonly string _newFile;
    private readonly string _hiddenFile;

    public DirectoryCleanupServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Directory.CreateDirectory(Path.Combine(_root, "nested", "emptyLeaf"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "hasHidden"));

        _oldFile = Path.Combine(_root, "data", "old.txt");
        _newFile = Path.Combine(_root, "data", "new.txt");
        _hiddenFile = Path.Combine(_root, "hasHidden", "secret.txt");

        File.WriteAllText(_oldFile, "0123456789");                 // 10 bytes
        File.WriteAllText(_newFile, "01234567890123456789");      // 20 bytes
        File.WriteAllText(_hiddenFile, "hidden");
        File.SetLastWriteTime(_oldFile, new DateTime(2020, 1, 1, 8, 0, 0));
        File.SetAttributes(_hiddenFile, FileAttributes.Hidden);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a leftover temp directory is harmless.
        }
    }

    private static CleanupOptions Options(
        bool recursive = true,
        bool includeHidden = false,
        int largestCount = 10,
        int staleDays = 30)
        => new(recursive, includeHidden, largestCount, staleDays, 1000);

    [Fact]
    public void FindsOnlyTrulyEmptyLeafFolders()
    {
        var result = DirectoryCleanupService.Scan(_root, Options());

        var relative = result.EmptyFolders.Select(entry => entry.RelativePath).ToList();
        Assert.Contains("empty", relative);
        Assert.Contains(Path.Combine("nested", "emptyLeaf"), relative);
        Assert.DoesNotContain("nested", relative);
        Assert.DoesNotContain("hasHidden", relative);
    }

    [Fact]
    public void ListsLargestFilesDescending()
    {
        var result = DirectoryCleanupService.Scan(_root, Options(largestCount: 1));

        Assert.Single(result.LargestFiles);
        Assert.Equal("new.txt", Path.GetFileName(result.LargestFiles[0].Path));
        Assert.Equal(20, result.LargestFiles[0].Size);
    }

    [Fact]
    public void StaleFilesRespectCutoff()
    {
        var result = DirectoryCleanupService.Scan(_root, Options(staleDays: 30));

        Assert.Equal(1, result.StaleFileCount);
        Assert.Contains(result.StaleFiles,
            entry => Path.GetFileName(entry.Path) == "old.txt");
        Assert.DoesNotContain(result.StaleFiles,
            entry => Path.GetFileName(entry.Path) == "new.txt");
    }

    [Fact]
    public void HiddenFilesAreCountedButNotListedByDefault()
    {
        var result = DirectoryCleanupService.Scan(_root, Options());

        // The hidden file is still part of the totals.
        Assert.Equal(3, result.FileCount);
        Assert.DoesNotContain(result.LargestFiles,
            entry => Path.GetFileName(entry.Path) == "secret.txt");

        var withHidden = DirectoryCleanupService.Scan(_root, Options(includeHidden: true));
        Assert.Contains(withHidden.LargestFiles,
            entry => Path.GetFileName(entry.Path) == "secret.txt");
    }

    [Fact]
    public void NonRecursiveScanStopsAtTopLevel()
    {
        var result = DirectoryCleanupService.Scan(_root, Options(recursive: false));

        Assert.Equal(0, result.FileCount);
        Assert.Contains(result.EmptyFolders, entry => entry.RelativePath == "empty");
        Assert.DoesNotContain(result.EmptyFolders,
            entry => entry.RelativePath == Path.Combine("nested", "emptyLeaf"));
    }

    [Fact]
    public void StaleListIsCappedButCountIsComplete()
    {
        for (var index = 0; index < 5; index++)
        {
            var path = Path.Combine(_root, "data", $"s{index}.txt");
            File.WriteAllText(path, "x");
            File.SetLastWriteTime(path, new DateTime(2019, 1, 1));
        }

        var result = DirectoryCleanupService.Scan(_root,
            new CleanupOptions(true, false, 10, 30, 2));

        Assert.Equal(6, result.StaleFileCount);
        Assert.Equal(2, result.StaleFiles.Count);
        Assert.Equal(15, result.StaleTotalSize); // old.txt 10 bytes + five 1-byte files
    }

    [Fact]
    public void CancellationStopsScan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DirectoryCleanupService.Scan(_root, Options(), cancellation.Token));
    }

    [Fact]
    public void MissingRootThrows()
    {
        var missing = Path.Combine(_root, "missing");

        Assert.Throws<DirectoryNotFoundException>(() =>
            DirectoryCleanupService.Scan(missing, Options()));
    }
}
