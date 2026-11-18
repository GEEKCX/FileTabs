using System.IO;

namespace FileTabs;

/// <summary>
/// Picks the first free "新建文件夹 (N)" name. Directory.Exists/File.Exists
/// are synchronous and can block for a network timeout, so callers must run
/// this off the UI thread; the probe cap stops a pathological folder (or a
/// stale share listing) from turning the search into an endless loop.
/// </summary>
public static class NewFolderProbe
{
    /// <summary>Names checked before giving up, including the unsuffixed one.</summary>
    public const int MaxProbe = 1000;

    /// <summary>
    /// Create a uniquely named folder inside <paramref name="folder"/> and
    /// return its path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// More than <see cref="MaxProbe"/> candidate names are already taken.
    /// </exception>
    public static string CreateNewFolder(string folder)
    {
        var candidate = FindAvailableName(folder);
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>
    /// Return a path inside <paramref name="folder"/> that does not exist
    /// yet; nothing is created.
    /// </summary>
    public static string FindAvailableName(string folder)
    {
        ThreadGuard.AssertBackground(nameof(FindAvailableName));

        const string name = "新建文件夹";
        var candidate = Path.Combine(folder, name);
        for (var suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
        {
            if (suffix > MaxProbe)
            {
                throw new InvalidOperationException(
                    $"该目录下同名文件夹过多（超过 {MaxProbe} 个），请手动命名");
            }
            candidate = Path.Combine(folder, $"{name} ({suffix})");
        }
        return candidate;
    }
}
