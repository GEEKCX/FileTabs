using System.IO;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;

namespace FileTabs;

public sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class SessionData
{
    public List<string> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public List<string> RecentlyClosed { get; set; } = new();
}

public sealed class Workspace
{
    public string Name { get; set; } = "";
    public List<string> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public DateTime LastUsedAt { get; set; }
}

/// <summary>
/// App-wide user settings. Current is a lazy singleton: loaded once on first
/// access, then treated as the authoritative in-memory state for the process.
/// Persisted explicitly via AppStorage.SaveSettings.
/// </summary>
public sealed class AppSettings
{
    public static AppSettings Current { get; } = Load();

    public string NewTabPath { get; set; } = BrowserTab.ThisPc;

    // Interface options persisted with the rest of the settings.
    public bool ShowBookmarkBar { get; set; } = true;

    public string Theme { get; set; } = nameof(AppTheme.FollowSystem);

    public bool SearchSortByName { get; set; }

    public bool ShowPreviewPane { get; set; }

    private static AppSettings Load()
    {
        var settings = AppStorage.LoadSettings();
        return settings ?? new AppSettings();
    }
}

/// <summary>
/// Owns all app data under %APPDATA%\FileTabs: bookmarks and session.
/// JSON is kept human-readable (no escaping of CJK characters).
/// Writes are atomic: save to a temp file, flush to disk, then rename over
/// the target so a crash cannot leave a half-written JSON behind.
/// Failures are logged to app.log instead of interrupting the caller.
/// </summary>
public static class AppStorage
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileTabs");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // UTF-8 without BOM keeps the file compact and diff-friendly.
    private static readonly Encoding JsonFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static List<Bookmark> LoadBookmarks()
    {
        var file = Path.Combine(DataDirectory, "bookmarks.json");
        if (!File.Exists(file)) return new List<Bookmark>();
        try
        {
            return JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(file), JsonOptions)
                   ?? new List<Bookmark>();
        }
        catch (JsonException)
        {
            ArchiveCorruptFile(file);
            Log("bookmarks.json is corrupt; ignored.");
            return new List<Bookmark>();
        }
    }

    public static void SaveBookmarks(IEnumerable<Bookmark> bookmarks)
    {
        try
        {
            WriteFileAtomically(Path.Combine(DataDirectory, "bookmarks.json"),
                JsonSerializer.Serialize(bookmarks, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveBookmarks failed: {ex.Message}");
        }
    }

    public static SessionData? LoadSession()
    {
        var file = Path.Combine(DataDirectory, "session.json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(file), JsonOptions);
        }
        catch (JsonException)
        {
            ArchiveCorruptFile(file);
            Log("session.json is corrupt; ignored.");
            return null;
        }
    }

    public static void SaveSession(SessionData session)
    {
        try
        {
            WriteFileAtomically(Path.Combine(DataDirectory, "session.json"),
                JsonSerializer.Serialize(session, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveSession failed: {ex.Message}");
        }
    }

    public static List<Workspace> LoadWorkspaces()
    {
        var file = Path.Combine(DataDirectory, "workspaces.json");
        if (!File.Exists(file)) return new List<Workspace>();
        try
        {
            return JsonSerializer.Deserialize<List<Workspace>>(File.ReadAllText(file), JsonOptions)
                   ?? new List<Workspace>();
        }
        catch (JsonException)
        {
            ArchiveCorruptFile(file);
            Log("workspaces.json is corrupt; ignored.");
            return new List<Workspace>();
        }
    }

    public static void SaveWorkspaces(IEnumerable<Workspace> workspaces)
    {
        try
        {
            WriteFileAtomically(Path.Combine(DataDirectory, "workspaces.json"),
                JsonSerializer.Serialize(workspaces, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveWorkspaces failed: {ex.Message}");
        }
    }

    public static AppSettings? LoadSettings()
    {
        var file = Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(file)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions)
                   ?? new AppSettings();
        }
        catch (JsonException)
        {
            ArchiveCorruptFile(file);
            Log("settings.json is corrupt; ignored.");
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            WriteFileAtomically(Path.Combine(DataDirectory, "settings.json"),
                JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveSettings failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Write to a unique temp file so concurrent saves never see a partial
    /// payload, then swap it over the target. The rename is atomic on NTFS.
    /// </summary>
    private static void WriteFileAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, JsonFileEncoding))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Rename a corrupt JSON aside so the user's data is not silently erased.</summary>
    private static void ArchiveCorruptFile(string file)
    {
        try
        {
            File.Move(file, $"{file}.bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Failed to archive corrupt file {file}: {ex.Message}");
        }
    }

    /// <summary>Best-effort append-only log for data failures we deliberately swallow.</summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "app.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                JsonFileEncoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a log line must not crash the app.
        }
    }
}
