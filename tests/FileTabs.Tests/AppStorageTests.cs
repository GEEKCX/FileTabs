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

    [Fact]
    public void SessionRoundTripsAndUsesCompactJson()
    {
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
        var file = Path.Combine(AppStorage.DataDirectory, "session.json");
        File.WriteAllText(file, "{ not json");

        Assert.Null(AppStorage.LoadSession());
        Assert.True(File.Exists(file + ".bad"));
    }

    [Fact]
    public void OversizedLogIsRotated()
    {
        var file = Path.Combine(AppStorage.DataDirectory, "app.log");
        File.WriteAllText(file, new string('x', 1024 * 1024));

        AppStorage.Log("after rotation");

        Assert.True(File.Exists(file + ".1"));
        Assert.True(new FileInfo(file).Length < 1024 * 1024);
    }
}
