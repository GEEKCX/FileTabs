using System.IO;
using FileTabs;

namespace FileTabs.Tests;

/// <summary>
/// AppStorage fixes its data directory on first use, so the fixture must set
/// FILETABS_DATA_DIR before any test touches the type.
/// </summary>
public sealed class AppStorageFixture : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), $"filetabs-tests-{Guid.NewGuid():N}");

    public AppStorageFixture()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("FILETABS_DATA_DIR", Root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FILETABS_DATA_DIR", null);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is harmless.
        }
    }
}

public sealed class AppStorageTests : IClassFixture<AppStorageFixture>
{
    private readonly AppStorageFixture _fixture;

    public AppStorageTests(AppStorageFixture fixture) => _fixture = fixture;

    // AppStorage owns fixed file names and the fixture is shared by the whole
    // class, so each test starts from an empty data directory.
    private static void ClearDataDirectory()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(AppStorage.DataDirectory))
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
    }

    [Fact]
    public void SessionRoundTripsAndUsesCompactJson()
    {
        ClearDataDirectory();

        // Guard: a fixture-ordering mistake must not write into the real
        // signed-in user's profile.
        Assert.Equal(
            Path.GetFullPath(_fixture.Root),
            AppStorage.DataDirectory);

        var session = new SessionData
        {
            Tabs = [@"C:\Windows", @"C:\Users"],
            ActiveIndex = 1,
            RecentlyClosed = [@"C:\Temp"],
            Pinned = [true, false],
            Titles = ["Windows", "Users"],
        };

        AppStorage.SaveSession(session);

        var text = File.ReadAllText(Path.Combine(AppStorage.DataDirectory, "session.json"));
        Assert.DoesNotContain("\n", text);

        var loaded = Assert.IsType<SessionData>(AppStorage.LoadSession());
        Assert.Equal(session.Tabs, loaded.Tabs);
        Assert.Equal(1, loaded.ActiveIndex);
        Assert.Equal(session.RecentlyClosed, loaded.RecentlyClosed);
        Assert.Equal(session.Pinned, loaded.Pinned);
        Assert.Equal(session.Titles, loaded.Titles);
    }

    [Fact]
    public void RecentLocationsRoundTrip()
    {
        ClearDataDirectory();

        var visited = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Local);
        var locations = new List<RecentLocation>
        {
            new() { Path = @"C:\Windows", VisitedAt = visited },
            new() { Path = @"C:\Users", VisitedAt = visited.AddMinutes(-5) },
        };

        AppStorage.SaveRecentLocations(locations);

        var loaded = AppStorage.LoadRecentLocations();
        Assert.Equal(locations.Count, loaded.Count);
        Assert.Equal(@"C:\Windows", loaded[0].Path);
        Assert.Equal(visited, loaded[0].VisitedAt);
        Assert.Equal(@"C:\Users", loaded[1].Path);
    }

    [Fact]
    public void CorruptSessionIsArchivedAndIgnored()
    {
        ClearDataDirectory();

        var file = Path.Combine(AppStorage.DataDirectory, "session.json");
        File.WriteAllText(file, "{ not json");

        Assert.Null(AppStorage.LoadSession());
        Assert.True(File.Exists(file + ".bad"));
    }

    [Fact]
    public void SaveKeepsPreviousVersionAsBackup()
    {
        ClearDataDirectory();
        var file = Path.Combine(AppStorage.DataDirectory, "session.json");

        AppStorage.SaveSession(new SessionData { Tabs = ["old"] });
        AppStorage.SaveSession(new SessionData { Tabs = ["new"] });

        var backup = File.ReadAllText(file + ".bak");
        Assert.Contains("\"old\"", backup);
        var loaded = Assert.IsType<SessionData>(AppStorage.LoadSession());
        Assert.Equal(new[] { "new" }, loaded.Tabs);
    }

    [Fact]
    public void CorruptPrimaryIsRestoredFromBackup()
    {
        ClearDataDirectory();
        var file = Path.Combine(AppStorage.DataDirectory, "session.json");
        AppStorage.SaveSession(new SessionData { Tabs = ["old"] });
        AppStorage.SaveSession(new SessionData { Tabs = ["new"] });
        File.WriteAllText(file, "{ not json");

        var loaded = Assert.IsType<SessionData>(AppStorage.LoadSession());

        Assert.Equal(new[] { "old" }, loaded.Tabs);
        Assert.True(File.Exists(file + ".bad"));
        // The backup is promoted back to the primary path, so the next save
        // rotates from a known-good file instead of backing up the corruption.
        Assert.IsType<SessionData>(AppStorage.LoadSession());
    }

    [Fact]
    public void MissingPrimaryIsRestoredFromBackup()
    {
        ClearDataDirectory();
        var file = Path.Combine(AppStorage.DataDirectory, "session.json");
        AppStorage.SaveSession(new SessionData { Tabs = ["old"] });
        AppStorage.SaveSession(new SessionData { Tabs = ["new"] });
        File.Delete(file);

        var loaded = Assert.IsType<SessionData>(AppStorage.LoadSession());

        Assert.Equal(new[] { "old" }, loaded.Tabs);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void CorruptPrimaryAndBackupReturnNoSession()
    {
        ClearDataDirectory();
        var file = Path.Combine(AppStorage.DataDirectory, "session.json");
        AppStorage.SaveSession(new SessionData { Tabs = ["old"] });
        AppStorage.SaveSession(new SessionData { Tabs = ["new"] });
        File.WriteAllText(file, "{ not json");
        File.WriteAllText(file + ".bak", "{ not json");

        Assert.Null(AppStorage.LoadSession());
        Assert.True(File.Exists(file + ".bad"));
    }

    [Fact]
    public void FailedBackupKeepsPrimaryUnchanged()
    {
        ClearDataDirectory();
        var file = Path.Combine(AppStorage.DataDirectory, "session.json");
        AppStorage.SaveSession(new SessionData { Tabs = ["old"] });

        // Hold the backup path so both File.Replace and the copy fallback
        // fail. The save must leave the primary file untouched.
        using (var lockedBackup = new FileStream(
            file + ".bak", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            AppStorage.SaveSession(new SessionData { Tabs = ["new"] });
        }

        var loaded = Assert.IsType<SessionData>(AppStorage.LoadSession());
        Assert.Equal(new[] { "old" }, loaded.Tabs);
    }

    [Fact]
    public void ClearingRenameHistoryRemovesBackup()
    {
        ClearDataDirectory();
        AppStorage.SaveRenameHistory(new RenameHistory
        {
            Items = [new RenameHistoryItem { OldPath = "a", NewPath = "b" }],
        });
        AppStorage.SaveRenameHistory(new RenameHistory
        {
            Items = [new RenameHistoryItem { OldPath = "c", NewPath = "d" }],
        });
        Assert.True(File.Exists(AppStorage.RenameHistoryPath + ".bak"));

        AppStorage.ClearRenameHistory();

        Assert.Null(AppStorage.LoadRenameHistory());
        Assert.False(File.Exists(AppStorage.RenameHistoryPath + ".bak"));
    }

    [Fact]
    public void RestoreLatestBackupsRestoresPreviousVersion()
    {
        ClearDataDirectory();
        var file = Path.Combine(AppStorage.DataDirectory, "session.json");
        AppStorage.SaveSession(new SessionData { Tabs = ["old"] });
        AppStorage.SaveSession(new SessionData { Tabs = ["new"] });

        var restored = AppStorage.RestoreLatestBackups();

        Assert.Equal(1, restored);
        var loaded = Assert.IsType<SessionData>(AppStorage.LoadSession());
        Assert.Equal(new[] { "old" }, loaded.Tabs);
        Assert.True(File.Exists(file + ".bak"));
    }

    [Fact]
    public void RestoreLatestBackupsSkipsCorruptBackup()
    {
        ClearDataDirectory();
        var file = Path.Combine(AppStorage.DataDirectory, "session.json");
        AppStorage.SaveSession(new SessionData { Tabs = ["old"] });
        AppStorage.SaveSession(new SessionData { Tabs = ["new"] });
        File.WriteAllText(file + ".bak", "{ not json");

        var restored = AppStorage.RestoreLatestBackups();

        Assert.Equal(0, restored);
        var loaded = Assert.IsType<SessionData>(AppStorage.LoadSession());
        Assert.Equal(new[] { "new" }, loaded.Tabs);
    }

    [Fact]
    public void LogCrashWritesSeparateLog()
    {
        ClearDataDirectory();

        AppStorage.LogCrash("test crash", new InvalidOperationException("boom"));

        var text = File.ReadAllText(
            Path.Combine(AppStorage.DataDirectory, "crash.log"));
        Assert.Contains("test crash", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void OversizedLogIsRotated()
    {
        ClearDataDirectory();
        var file = Path.Combine(AppStorage.DataDirectory, "app.log");
        File.WriteAllText(file, new string('x', 1024 * 1024));

        AppStorage.Log("after rotation");

        Assert.True(File.Exists(file + ".1"));
        Assert.True(new FileInfo(file).Length < 1024 * 1024);
    }
}
