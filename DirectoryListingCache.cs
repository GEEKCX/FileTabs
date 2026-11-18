using System.Collections.Concurrent;
using System.IO;

namespace FileTabs;

/// <summary>One item of a directory listing.</summary>
public sealed record DirectoryListingEntry(string FullName, string Name, bool IsFolder);

/// <summary>
/// Result of a directory scan. <see cref="Complete"/> is false when the
/// scanner stopped at a cap or hit an error, so a partial listing is never
/// cached as if it were the whole folder.
/// </summary>
public readonly record struct DirectoryListingScan(
    IReadOnlyList<DirectoryListingEntry> Entries, bool Complete);

/// <summary>
/// Short-lived cache of complete directory listings, built like
/// <see cref="FolderSizeCache"/>: entries expire after a few seconds, file
/// operations drop them eagerly, and concurrent scans of the same folder are
/// coalesced. The address bar autocomplete re-reads the same parent on every
/// keystroke; a five-second window keeps that off the disk without making
/// renames or deletions invisible for long.
/// </summary>
public static class DirectoryListingCache
{
    private sealed record Entry(
        IReadOnlyList<DirectoryListingEntry> Entries, DateTimeOffset CreatedAt);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(
        StringComparer.OrdinalIgnoreCase);

    // Network paths (UNC) can take seconds per query and may wake sleeping
    // drives; listings there are never cached — callers just scan directly.
    // Directory contents change faster than recursive sizes, so the window is
    // shorter than FolderSizeCache's 30s.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    // Long sessions visit many folders; cap the number of cached listings so
    // the dictionary cannot grow unbounded. Oldest entries go first, the
    // same simple policy FolderSizeCache uses.
    private const int Capacity = 2000;

    /// <summary>Number of folders currently being scanned.</summary>
    public static int PendingCount => Pending.Count;

    /// <summary>Number of cached listings (including not-yet-expired ones).</summary>
    public static int Count => Cache.Count;

    private static readonly ConcurrentDictionary<string, Lazy<DirectoryListingScan>> Pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Get a cached listing. Returns null when unknown or expired; does NOT
    /// start a scan — call <see cref="GetOrScan"/> for that.
    /// </summary>
    public static IReadOnlyList<DirectoryListingEntry>? TryGet(string folder)
    {
        if (!Cache.TryGetValue(folder, out var entry)) return null;
        if (DateTimeOffset.UtcNow - entry.CreatedAt >= Ttl)
        {
            // Drop it rather than leaving expired entries to occupy capacity.
            Cache.TryRemove(new KeyValuePair<string, Entry>(folder, entry));
            return null;
        }
        return entry.Entries;
    }

    /// <summary>
    /// Return the cached listing for <paramref name="folder"/>, or run
    /// <paramref name="scan"/> once for all concurrent callers of the same
    /// folder and cache the result when the scan reports it complete.
    /// </summary>
    public static DirectoryListingScan GetOrScan(
        string folder, Func<DirectoryListingScan> scan)
    {
        if (TryGet(folder) is { } cached)
        {
            return new DirectoryListingScan(cached, Complete: true);
        }
        if (!CanCache(folder))
        {
            // UNC: never share or store, each caller scans for itself.
            return scan();
        }

        // Coalesce concurrent scans: two keystrokes for the same parent share
        // one enumeration instead of hammering the disk twice. The entry is
        // removed in finally so a failed scan does not poison later calls.
        var pending = Pending.GetOrAdd(
            folder,
            _ => new Lazy<DirectoryListingScan>(
                scan, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var result = pending.Value;
            if (result.Complete) Store(folder, result.Entries);
            return result;
        }
        finally
        {
            Pending.TryRemove(
                new KeyValuePair<string, Lazy<DirectoryListingScan>>(folder, pending));
        }
    }

    /// <summary>
    /// Invalidate a folder (or the whole cache with null) after file
    /// operations so autocomplete does not show a stale listing.
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

    private static void Store(string folder, IReadOnlyList<DirectoryListingEntry> entries)
    {
        if (!CanCache(folder)) return;
        // Purge on every store, not only at capacity: expired listings would
        // otherwise pin their entries in memory for the whole session.
        TrimCache();
        Cache[folder] = new Entry(entries, DateTimeOffset.UtcNow);
    }

    private static void TrimCache()
    {
        // Expired entries cost nothing to drop and usually free the slot.
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in Cache)
        {
            if (now - pair.Value.CreatedAt >= Ttl)
            {
                Cache.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value));
            }
        }

        // Keep one slot for the entry Store is about to add. Evict by
        // creation time; the TryRemove result guards against a concurrent
        // Invalidate(null) emptying the dictionary between Count and removal.
        while (Cache.Count >= Capacity)
        {
            var removed = false;
            foreach (var pair in Cache.OrderBy(pair => pair.Value.CreatedAt))
            {
                if (Cache.TryRemove(pair.Key, out _))
                {
                    removed = true;
                    break;
                }
            }
            if (!removed) break;
        }
    }

    private static bool CanCache(string folder)
    {
        return !folder.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
