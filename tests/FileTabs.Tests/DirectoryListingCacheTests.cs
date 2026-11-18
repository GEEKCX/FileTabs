using System.IO;
using FileTabs;

namespace FileTabs.Tests;

public sealed class DirectoryListingCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"filetabs-listing-{Guid.NewGuid():N}");

    public DirectoryListingCacheTests()
    {
        Directory.CreateDirectory(_root);
        // The cache is static; start every test from a known-empty state.
        DirectoryListingCache.Invalidate(null);
    }

    public void Dispose()
    {
        DirectoryListingCache.Invalidate(null);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a leftover temp directory is harmless.
        }
    }

    private static DirectoryListingScan CompleteListing(params string[] names)
    {
        var entries = names
            .Select(name => new DirectoryListingEntry(
                Path.Combine(@"C:\listing", name), name, IsFolder: false))
            .ToArray();
        return new DirectoryListingScan(entries, Complete: true);
    }

    private string Folder(string name) => Path.Combine(_root, name);

    [Fact]
    public void Invalidate_single_folder_drops_only_that_listing()
    {
        var first = Folder("first");
        var second = Folder("second");
        DirectoryListingCache.GetOrScan(first, () => CompleteListing("a.txt"));
        DirectoryListingCache.GetOrScan(second, () => CompleteListing("b.txt"));

        DirectoryListingCache.Invalidate(first);

        Assert.Null(DirectoryListingCache.TryGet(first));
        Assert.NotNull(DirectoryListingCache.TryGet(second));
    }

    [Fact]
    public void Invalidate_null_clears_every_listing()
    {
        DirectoryListingCache.GetOrScan(Folder("first"), () => CompleteListing("a.txt"));
        DirectoryListingCache.GetOrScan(Folder("second"), () => CompleteListing("b.txt"));

        DirectoryListingCache.Invalidate(null);

        Assert.Null(DirectoryListingCache.TryGet(Folder("first")));
        Assert.Null(DirectoryListingCache.TryGet(Folder("second")));
        Assert.Equal(0, DirectoryListingCache.Count);
    }

    [Fact]
    public void Incomplete_scan_is_not_cached()
    {
        var folder = Folder("partial");

        var scan = DirectoryListingCache.GetOrScan(
            folder, () => new DirectoryListingScan(CompleteListing("a.txt").Entries, Complete: false));

        Assert.Single(scan.Entries);
        Assert.Null(DirectoryListingCache.TryGet(folder));
    }

    [Fact]
    public void Unc_paths_are_scanned_but_never_cached()
    {
        const string unc = @"\\server\share\folder";
        var scans = 0;

        DirectoryListingCache.GetOrScan(unc, () => { scans++; return CompleteListing("a.txt"); });
        DirectoryListingCache.GetOrScan(unc, () => { scans++; return CompleteListing("a.txt"); });

        // Each call scanned on its own: no cache entry, no pending coalescing.
        Assert.Equal(2, scans);
        Assert.Null(DirectoryListingCache.TryGet(unc));
    }

    [Fact]
    public void Capacity_evicts_the_oldest_listing()
    {
        // The spec caps the cache at 2000 listings; the first one must be
        // gone once a 2001st folder is stored.
        const int capacity = 2000;
        for (var i = 0; i <= capacity; i++)
        {
            var folder = Folder($"cap-{i}");
            DirectoryListingCache.GetOrScan(folder, () => CompleteListing($"f{i}.txt"));
        }

        Assert.Null(DirectoryListingCache.TryGet(Folder("cap-0")));
        Assert.NotNull(DirectoryListingCache.TryGet(Folder($"cap-{capacity}")));
        Assert.True(DirectoryListingCache.Count <= capacity);
    }
}
