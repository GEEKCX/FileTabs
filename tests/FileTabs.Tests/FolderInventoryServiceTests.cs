using System.IO;
using System.Text.Json;
using FileTabs;

namespace FileTabs.Tests;

public sealed class FolderInventoryServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"filetabs-inventory-{Guid.NewGuid():N}");

    public FolderInventoryServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "12345");
        File.WriteAllText(Path.Combine(_root, "sub", "b.md"), "abc");
    }

    public void Dispose()
    {
        try
        {
            // Hidden files block recursive delete on some systems.
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

    private static InventoryOptions Options(
        bool recursive = true,
        bool includeFiles = true,
        bool includeFolders = true,
        bool includeHidden = false)
        => new(recursive, includeFiles, includeFolders, includeHidden);

    [Fact]
    public void RecursiveScanCountsFilesFoldersAndSize()
    {
        var result = FolderInventoryService.Scan(_root, Options());

        Assert.Equal(2, result.FileCount);
        Assert.Equal(1, result.FolderCount);
        Assert.Equal(8, result.TotalSize);
        Assert.Equal(3, result.Entries.Count);
        Assert.Contains(result.Entries,
            entry => entry.RelativePath == Path.Combine("sub", "b.md") && entry.Size == 3);
        Assert.Contains(result.Entries,
            entry => entry.RelativePath == "sub" && entry.IsDirectory);
    }

    [Fact]
    public void NonRecursiveScanStillCountsTopLevelOnly()
    {
        var result = FolderInventoryService.Scan(_root, Options(recursive: false));

        Assert.Equal(1, result.FileCount);
        Assert.Equal(1, result.FolderCount);
        Assert.Equal(5, result.TotalSize);
        Assert.DoesNotContain(result.Entries, entry => entry.RelativePath.Contains("b.md"));
    }

    [Fact]
    public void HiddenItemsAreSkippedUnlessRequested()
    {
        var hidden = Path.Combine(_root, "hidden.txt");
        File.WriteAllText(hidden, "xy");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var withoutHidden = FolderInventoryService.Scan(_root, Options());
        Assert.DoesNotContain(withoutHidden.Entries, entry => entry.Name == "hidden.txt");

        var withHidden = FolderInventoryService.Scan(_root, Options(includeHidden: true));
        Assert.Contains(withHidden.Entries, entry => entry.Name == "hidden.txt");
    }

    [Fact]
    public void CsvQuotesFieldsWithCommas()
    {
        var commaDir = Path.Combine(_root, "a,b");
        Directory.CreateDirectory(commaDir);

        var result = FolderInventoryService.Scan(_root, Options());
        var csv = FolderInventoryService.Format(result, InventoryFormat.Csv);

        Assert.Contains("\"a,b\"", csv);
        Assert.StartsWith("相对路径,类型,大小(字节),修改时间,创建时间", csv);
    }

    [Fact]
    public void MarkdownContainsSummaryAndRows()
    {
        var result = FolderInventoryService.Scan(_root, Options());

        var markdown = FolderInventoryService.Format(result, InventoryFormat.Markdown);

        Assert.Contains("# 目录清单", markdown);
        Assert.Contains("| 相对路径 | 类型 | 大小 | 修改时间 |", markdown);
        Assert.Contains("a.txt", markdown);
        Assert.Contains("5 B", markdown);
    }

    [Fact]
    public void JsonIsValidAndCarriesCounts()
    {
        var result = FolderInventoryService.Scan(_root, Options());

        var json = FolderInventoryService.Format(result, InventoryFormat.Json);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(2, document.RootElement.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("folderCount").GetInt32());
        Assert.Equal(8, document.RootElement.GetProperty("totalSize").GetInt64());
        Assert.Equal(3, document.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void IncludeFilesFalseStillReportsTotals()
    {
        var result = FolderInventoryService.Scan(_root, Options(includeFiles: false));

        Assert.Equal(2, result.FileCount);
        Assert.Equal(8, result.TotalSize);
        Assert.All(result.Entries, entry => Assert.True(entry.IsDirectory));
    }

    [Fact]
    public void CancellationStopsScan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            FolderInventoryService.Scan(_root, Options(), cancellation.Token));
    }

    [Fact]
    public void MissingRootThrows()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        Assert.Throws<DirectoryNotFoundException>(() =>
            FolderInventoryService.Scan(missing, Options()));
    }

    [Fact]
    public void SizeFormattingUsesReadableUnits()
    {
        Assert.Equal("0 B", FolderInventoryService.FormatSize(0));
        Assert.Equal("1023 B", FolderInventoryService.FormatSize(1023));
        Assert.Equal("1 KB", FolderInventoryService.FormatSize(1024));
        Assert.Equal("1.5 KB", FolderInventoryService.FormatSize(1536));
        Assert.Equal("2 MB", FolderInventoryService.FormatSize(2 * 1024 * 1024));
    }
}
