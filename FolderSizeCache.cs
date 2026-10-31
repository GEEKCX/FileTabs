using System.Collections.Concurrent;
using System.IO;

namespace FileTabs;

/// <summary>
/// Background recursive folder size computation with a negative+positive
/// result cache. Sizes go stale as files change; entries expire after a
/// short TTL and any path touched by a shell operation is dropped eagerly
/// by the main window, so the status bar never shows week-old numbers.
/// </summary>
public static class FolderSizeCache
{
    private sealed record Entry(long Bytes, DateTimeOffset ComputedAt);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(
        StringComparer.OrdinalIgnoreCase);

    // Network paths (UNC) can take seconds per query and may wake sleeping
    // drives; sizes there are opt-in never — we simply refuse and the status
    // bar falls back to counting items only.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>Number of folders currently being computed.</summary>
    public static int PendingCount => Pending.Count;

    private static readonly ConcurrentDictionary<string, byte> Pending = new(
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Try to get a cached size. Returns null when unknown or expired; does
    /// NOT start computation — call <see cref="BeginCompute"/> for that.
    /// </summary>
    public static long? TryGet(string folder)
    {
        if (Cache.TryGetValue(folder, out var entry) &&
            DateTimeOffset.UtcNow - entry.ComputedAt < Ttl)
        {
            return entry.Bytes;
        }
        return null;
    }

    /// <summary>
    /// Start computing the size of a folder on the thread pool. When done the
    /// result lands in the cache; call <see cref="TryGet"/> again afterwards.
    /// Multiple concurrent requests for the same folder are coalesced.
    /// </summary>
    public static void BeginCompute(string folder)
    {
        if (!CanCompute(folder)) return;
        if (!Pending.TryAdd(folder, 0)) return; // already running

        // Copy the key: the caller's dictionary iteration may mutate.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var size = ComputeSize(folder);
                Cache[folder] = new Entry(size, DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                // Inaccessible subtree: cache a "known unknown" so we do not
                // hammer the same denied folder every status refresh.
                Cache[folder] = new Entry(-1, DateTimeOffset.UtcNow);
            }
            finally
            {
                Pending.TryRemove(folder, out var _);
            }
        });
    }

    /// <summary>
    /// Invalidate a folder (or the whole cache with null) after file
    /// operations so stale sizes are not shown.
    /// </summary>
    public static void Invalidate(string? folder)
    {
        if (folder is null)
        {
            Cache.Clear();
            return;
        }
        Cache.TryRemove(folder, out _);
    }

    private static bool CanCompute(string folder)
    {
        if (Cache.TryGetValue(folder, out var entry) &&
            DateTimeOffset.UtcNow - entry.ComputedAt < Ttl)
        {
            return false; // fresh entry already, nothing to do
        }
        // Refuse UNC/network paths — unbounded latency, may spin up a
        // disconnected drive.
        return !folder.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static long ComputeSize(string folder)
    {
        // EnumerationOptions with RecurseSubdirectories skips the manual
        // recursion stack and respects attributes we do not need to re-check.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // never loop through junctions/symlinks
        };
        long total = 0;
        var files = false;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            // Safe against files deleted between listing and stat.
            total += file.Length;
            files = true;
        }
        // A folder with zero enumerated files may also be genuinely empty;
        // -1 is reserved for "could not enumerate at all" above.
        return files || total > 0 ? total : 0;
    }
}
