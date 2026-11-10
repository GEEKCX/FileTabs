using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace FileTabs;

public sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    // Older bookmarks have no IsFolder field and do not break deserialization:
    // the initializer supplies the default. A missing target still renders as
    // a folder, matching 360's yellow folder glyph.
    public bool IsFolder { get; set; } = true;
}

public sealed class SessionData
{
    public List<string> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public List<string> RecentlyClosed { get; set; } = new();
    // Parallel to Tabs; kept separate so older session files still load.
    public List<bool> Pinned { get; set; } = new();
    // Parallel to Tabs. Lazy-restored tabs need a title before their shell
    // view exists; older files have no titles and fall back to the path name.
    public List<string> Titles { get; set; } = new();
}

public sealed class Workspace
{
    public string Name { get; set; } = "";
    public List<string> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
    public DateTime LastUsedAt { get; set; }
}

public sealed class RecentLocation
{
    public string Path { get; set; } = "";
    public DateTime VisitedAt { get; set; }
}

/// <summary>一次批量重命名中成功执行的一项，用于撤销。</summary>
public sealed class RenameHistoryItem
{
    public string OldPath { get; set; } = "";
    public string NewPath { get; set; } = "";
}

/// <summary>
/// 只保留最近一次批量重命名。撤销后即删除，避免旧记录在文件已经
/// 被移动或再次改名后误操作。
/// </summary>
public sealed class RenameHistory
{
    public DateTime At { get; set; }
    public List<RenameHistoryItem> Items { get; set; } = new();
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

    public bool ShowNativeSidebar { get; set; } = true;

    public bool ShowNativeCommandBar { get; set; }

    public string Theme { get; set; } = nameof(AppTheme.FollowSystem);

    public bool SearchSortByName { get; set; }

    public bool ShowPreviewPane { get; set; }

    public bool ShowDetailsPane { get; set; } = true;

    /// <summary>Turn on the Explorer takeover (phase 1: window hook only).</summary>
    public bool CaptureExplorer { get; set; }

    /// <summary>
    /// Phase 2 registry redirect: launch FileTabs.exe for double-clicked
    /// folders and drives. Only touches HKCU\Software\Classes; no admin
    /// rights needed and fully reversible.
    /// </summary>
    public bool RedirectFolders { get; set; }

    /// <summary>Enable the DropPoint drag-and-drop staging pad.</summary>
    public bool DropPointEnabled { get; set; } = true;

    /// <summary>Restore the previous tab session when the app starts.</summary>
    public bool RestoreLastSession { get; set; } = true;

    /// <summary>Terminal used by the "open in terminal" action.</summary>
    public string Terminal { get; set; } = nameof(TerminalKind.WindowsTerminal);

    /// <summary>
    /// Last normal window placement. NaN until the user has moved or resized
    /// the window, so the first run keeps the XAML default centered position.
    /// </summary>
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 640;
    public bool WindowMaximized { get; set; }

    private static AppSettings Load()
    {
        var settings = AppStorage.LoadSettings();
        return settings ?? new AppSettings();
    }
}

public enum TerminalKind
{
    WindowsTerminal,
    PowerShell,
    CommandPrompt,
    Warp,
}

/// <summary>
/// Owns all app data under %APPDATA%\FileTabs: bookmarks and session.
/// User-edited files are pretty-printed with CJK left unescaped; the two
/// machine files (session.json, history.json) are compact because they are
/// rewritten on navigation and never read by hand.
/// Writes are atomic: save to a temp file, flush to disk, then rename over
/// the target so a crash cannot leave a half-written JSON behind. Every
/// replacement also keeps the previous version as &lt;file&gt;.bak, and loading
/// falls back to that copy if the primary is missing or corrupt.
/// Failures are logged to app.log instead of interrupting the caller.
/// </summary>
public static class AppStorage
{
    // FILETABS_DATA_DIR lets automated checks run against a scratch profile
    // instead of the signed-in user's real session and history.
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("FILETABS_DATA_DIR") is { Length: > 0 } customDataDir
            ? Path.GetFullPath(customDataDir)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileTabs");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // WindowLeft/Top use NaN as the "no saved placement yet" sentinel.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // Same shape as JsonOptions without indentation, for files that are
    // written whole on every navigation.
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // UTF-8 without BOM keeps the file compact and diff-friendly.
    private static readonly Encoding JsonFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static List<Bookmark> LoadBookmarks()
    {
        var file = Path.Combine(DataDirectory, "bookmarks.json");
        return TryLoadJsonWithBackup<List<Bookmark>>(file, JsonOptions, out var bookmarks)
            ? bookmarks ?? new List<Bookmark>()
            : new List<Bookmark>();
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
        return TryLoadJsonWithBackup<SessionData>(file, CompactJsonOptions, out var session)
            ? session
            : null;
    }

    public static void SaveSession(SessionData session)
    {
        try
        {
            WriteFileLocked(Path.Combine(DataDirectory, "session.json"),
                JsonSerializer.Serialize(session, CompactJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveSession failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Snapshot-and-forget variant used while the window is running: the JSON
    /// is captured on the caller thread, the fsync and rename happen off it.
    /// Only the newest snapshot per file is written when several arrive
    /// while a write is already in flight.
    /// </summary>
    public static void SaveSessionAsync(SessionData session)
    {
        try
        {
            var json = JsonSerializer.Serialize(session, CompactJsonOptions);
            GetWriteQueue("session.json").Post(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log($"SaveSessionAsync failed: {ex.Message}");
        }
    }

    public static List<Workspace> LoadWorkspaces()
    {
        var file = Path.Combine(DataDirectory, "workspaces.json");
        return TryLoadJsonWithBackup<List<Workspace>>(file, JsonOptions, out var workspaces)
            ? workspaces ?? new List<Workspace>()
            : new List<Workspace>();
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

    public static string FolderTemplatePath =>
        Path.Combine(DataDirectory, "folder-templates.json");

    public static List<FolderTemplate> LoadFolderTemplates()
    {
        return TryLoadJsonWithBackup<List<FolderTemplate>>(
                FolderTemplatePath, JsonOptions, out var templates)
            ? templates ?? new List<FolderTemplate>()
            : new List<FolderTemplate>();
    }

    public static bool SaveFolderTemplates(IEnumerable<FolderTemplate> templates)
    {
        try
        {
            WriteFileAtomically(FolderTemplatePath,
                JsonSerializer.Serialize(templates, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveFolderTemplates failed: {ex.Message}");
            return false;
        }
    }

    public static string RenameHistoryPath =>
        Path.Combine(DataDirectory, "rename-history.json");

    public static RenameHistory? LoadRenameHistory()
    {
        return TryLoadJsonWithBackup<RenameHistory>(
                RenameHistoryPath, JsonOptions, out var history)
            ? history
            : null;
    }

    public static bool SaveRenameHistory(RenameHistory history)
    {
        try
        {
            WriteFileAtomically(RenameHistoryPath,
                JsonSerializer.Serialize(history, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveRenameHistory failed: {ex.Message}");
            return false;
        }
    }

    public static void ClearRenameHistory()
    {
        try
        {
            // The .bak must go too: otherwise the next load would restore the
            // history the user just cleared.
            foreach (var path in new[] { RenameHistoryPath, BackupPath(RenameHistoryPath) })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"ClearRenameHistory failed: {ex.Message}");
        }
    }

    public static List<RecentLocation> LoadRecentLocations()
    {
        var file = Path.Combine(DataDirectory, "history.json");
        return TryLoadJsonWithBackup<List<RecentLocation>>(file, CompactJsonOptions, out var locations)
            ? locations ?? new List<RecentLocation>()
            : new List<RecentLocation>();
    }

    public static void SaveRecentLocations(IEnumerable<RecentLocation> recentLocations)
    {
        try
        {
            WriteFileLocked(Path.Combine(DataDirectory, "history.json"),
                JsonSerializer.Serialize(recentLocations, CompactJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveRecentLocations failed: {ex.Message}");
        }
    }

    public static void SaveRecentLocationsAsync(IEnumerable<RecentLocation> recentLocations)
    {
        try
        {
            var json = JsonSerializer.Serialize(recentLocations, CompactJsonOptions);
            GetWriteQueue("history.json").Post(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log($"SaveRecentLocationsAsync failed: {ex.Message}");
        }
    }

    /// <summary>Lets the close path wait for an in-flight background write.</summary>
    public static void WaitForPendingWrites(TimeSpan timeout)
    {
        foreach (var queue in WriteQueues.Values)
        {
            queue.WaitForIdle(timeout);
        }
    }

    private static readonly string[] RestorableFileNames =
    {
        "session.json",
        "bookmarks.json",
        "workspaces.json",
        "folder-templates.json",
        "rename-history.json",
        "settings.json",
        "history.json",
    };

    /// <summary>True when at least one of the data files has a .bak to restore.</summary>
    public static bool HasLatestBackups()
    {
        foreach (var fileName in RestorableFileNames)
        {
            if (File.Exists(BackupPath(Path.Combine(DataDirectory, fileName))))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Overwrite every primary JSON with its .bak after validating the backup.
    /// Used by the settings-page recovery entry. Returns the number of files
    /// restored; the backup itself is left in place so the operation can be
    /// inspected afterwards.
    /// </summary>
    public static int RestoreLatestBackups()
    {
        var restored = 0;
        foreach (var fileName in RestorableFileNames)
        {
            var file = Path.Combine(DataDirectory, fileName);
            var backup = BackupPath(file);
            if (!File.Exists(backup)) continue;
            try
            {
                var content = File.ReadAllText(backup);
                // A corrupt backup must never replace a readable primary.
                using var _ = JsonDocument.Parse(content);
                lock (WriteLock)
                {
                    WriteFileAtomically(file, content, keepPreviousVersion: false);
                }
                restored++;
                Log($"{fileName} restored from backup by user request.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log($"Failed to restore {fileName} from backup: {ex.Message}");
            }
        }
        return restored;
    }

    public static bool TryOpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = DataDirectory,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log($"Failed to open data directory: {ex.Message}");
            return false;
        }
    }

    // Guards the temp-file + rename step so a synchronous save (settings,
    // bookmarks, final session flush) cannot race a queued background write.
    private static readonly object WriteLock = new();
    private static readonly Dictionary<string, LatestWriteQueue> WriteQueues = new(StringComparer.Ordinal);

    private static LatestWriteQueue GetWriteQueue(string fileName)
    {
        lock (WriteQueues)
        {
            if (!WriteQueues.TryGetValue(fileName, out var queue))
            {
                queue = new LatestWriteQueue(Path.Combine(DataDirectory, fileName));
                WriteQueues[fileName] = queue;
            }
            return queue;
        }
    }

    private static void WriteFileLocked(string path, string content)
    {
        lock (WriteLock)
        {
            WriteFileAtomically(path, content);
        }
    }

    // Writes the most recent snapshot at a time: while one fsync runs, newer
    // snapshots replace the pending payload instead of queueing behind it.
    private sealed class LatestWriteQueue
    {
        private readonly string _path;
        private readonly object _gate = new();
        private string? _pendingContent;
        private Task _pump = Task.CompletedTask;
        private bool _running;

        public LatestWriteQueue(string path) => _path = path;

        public void Post(string content)
        {
            lock (_gate)
            {
                _pendingContent = content;
                if (_running) return;
                _running = true;
                _pump = Task.Run(PumpAsync);
            }
        }

        public void WaitForIdle(TimeSpan timeout)
        {
            Task pump;
            lock (_gate) pump = _pump;
            try
            {
                pump.Wait(timeout);
            }
            catch (AggregateException ex)
            {
                Log($"Background write did not finish: {ex.GetBaseException().Message}");
            }
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                string? content;
                lock (_gate)
                {
                    content = _pendingContent;
                    _pendingContent = null;
                    if (content is null)
                    {
                        _running = false;
                        return;
                    }
                }
                try
                {
                    await Task.Run(() => WriteFileLocked(_path, content));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Background write to {Path.GetFileName(_path)} failed: {ex.Message}");
                }
            }
        }
    }

    public static AppSettings? LoadSettings()
    {
        var file = Path.Combine(DataDirectory, "settings.json");
        return TryLoadJsonWithBackup<AppSettings>(file, JsonOptions, out var settings)
            ? settings ?? new AppSettings()
            : new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        // Window placement starts as NaN until the user's first move. It can
        // also still be NaN while startup restores it, and System.Text.Json
        // refuses to write NaN, so write 0 ("no saved position") instead.
        var windowLeft = settings.WindowLeft;
        var windowTop = settings.WindowTop;
        if (!double.IsFinite(windowLeft)) settings.WindowLeft = 0;
        if (!double.IsFinite(windowTop)) settings.WindowTop = 0;
        try
        {
            WriteFileAtomically(Path.Combine(DataDirectory, "settings.json"),
                JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"SaveSettings failed: {ex.Message}");
        }
        finally
        {
            settings.WindowLeft = windowLeft;
            settings.WindowTop = windowTop;
        }
    }

    /// <summary>
    /// Load a data file, falling back to the copy saved before the previous
    /// successful write. A primary file that fails to parse is archived as
    /// .bad first, then the .bak is promoted back to the primary path so the
    /// next save rotates from a known-good file instead of a corrupt one.
    /// </summary>
    private static bool TryLoadJsonWithBackup<T>(
        string file, JsonSerializerOptions options, out T? value)
    {
        var backup = BackupPath(file);
        var primaryExists = File.Exists(file);

        if (TryReadJson(file, options, out value, out var primaryError))
        {
            return true;
        }

        if (primaryExists && primaryError is JsonException)
        {
            ArchiveCorruptFile(file);
        }

        if (TryReadJson(backup, options, out value, out var backupError))
        {
            Log($"{Path.GetFileName(file)} was restored from {Path.GetFileName(backup)}.");
            TryRestorePrimary(file, backup);
            return true;
        }

        if (primaryError is JsonException)
        {
            Log($"{Path.GetFileName(file)} is corrupt; ignored.");
        }
        else if (primaryError is not null)
        {
            Log($"{Path.GetFileName(file)} could not be read: {primaryError.Message}");
        }
        if (backupError is JsonException)
        {
            Log($"{Path.GetFileName(backup)} is corrupt; ignored.");
        }

        return false;
    }

    private static bool TryReadJson<T>(
        string file, JsonSerializerOptions options, out T? value, out Exception? error)
    {
        value = default;
        error = null;
        if (!File.Exists(file)) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(file), options);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex;
            return false;
        }
    }

    private static void TryRestorePrimary(string file, string backup)
    {
        try
        {
            // No backup rotation here: the valid backup must survive even if
            // this restore fails.
            WriteFileAtomically(file, File.ReadAllText(backup), keepPreviousVersion: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Failed to restore {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    private static string BackupPath(string path) => path + ".bak";

    private static bool TryCreateBackup(string path, string backup)
    {
        try
        {
            File.Copy(path, backup, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Failed to back up {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write to a unique temp file so concurrent saves never see a partial
    /// payload, then swap it over the target. The rename is atomic on NTFS.
    /// The previous version is kept as &lt;file&gt;.bak: File.Replace does the
    /// swap and backup in one step; the copy+move fallback still refuses to
    /// overwrite the primary when the backup cannot be created.
    /// </summary>
    private static void WriteFileAtomically(
        string path, string content, bool keepPreviousVersion = true)
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

            if (!keepPreviousVersion || !File.Exists(path))
            {
                File.Move(temp, path, overwrite: true);
                return;
            }

            var backup = BackupPath(path);
            try
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                // File.Replace is unavailable on some file systems. Copy the
                // old file first; if even that fails, keep the primary intact
                // rather than writing without a recoverable previous version.
                if (!TryCreateBackup(path, backup))
                {
                    throw new IOException(
                        $"Could not back up {Path.GetFileName(path)}; previous file kept.", ex);
                }
                File.Move(temp, path, overwrite: true);
            }
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
    public static void Log(string message) => AppendLogLine("app.log", message);

    /// <summary>
    /// Separate, longer-lived record for unhandled exceptions so a crash can
    /// be diagnosed even after app.log has rotated.
    /// </summary>
    public static void LogCrash(string message, Exception? exception = null)
    {
        var details = exception is null
            ? message
            : $"{message}{Environment.NewLine}{exception}";
        AppendLogLine("crash.log", details);
    }

    private static void AppendLogLine(string fileName, string message)
    {
        // Crash handlers run on different threads; serialize appends so two
        // concurrent File.AppendAllText calls cannot fail each other.
        lock (LogLock)
        {
            AppendLogLineCore(fileName, message);
        }
    }

    private static readonly object LogLock = new();

    private static void AppendLogLineCore(string fileName, string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var logPath = Path.Combine(DataDirectory, fileName);
        // Logs are append-only and only written on failures; cap each so a
        // long-lived install cannot grow one without bound. One previous file
        // is enough to keep the context around a recent failure. Rotation is
        // best-effort: if it fails (another process holds the file), still
        // append below instead of dropping the line.
        try
        {
            var info = new FileInfo(logPath);
            if (info.Exists && info.Length >= MaxLogBytes)
            {
                File.Move(logPath, logPath + ".1", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep logging into the current file; retry next time.
        }

        try
        {
            File.AppendAllText(logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                JsonFileEncoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a log line must not crash the app.
        }
    }

    private const long MaxLogBytes = 1024 * 1024;
}
