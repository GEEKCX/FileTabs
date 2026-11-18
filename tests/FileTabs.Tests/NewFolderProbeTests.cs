using System.IO;
using FileTabs;

namespace FileTabs.Tests;

public sealed class NewFolderProbeTests : IDisposable
{
    private const string BaseName = "新建文件夹";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"filetabs-probe-{Guid.NewGuid():N}");

    public NewFolderProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a leftover temp directory is harmless.
        }
    }

    private string Taken(int suffix) => Path.Combine(_root, $"{BaseName} ({suffix})");

    [Fact]
    public void FindAvailableName_returns_first_free_suffix()
    {
        Directory.CreateDirectory(Path.Combine(_root, BaseName));
        Directory.CreateDirectory(Taken(2));
        Directory.CreateDirectory(Taken(3));

        var candidate = NewFolderProbe.FindAvailableName(_root);

        Assert.Equal(Taken(4), candidate);
    }

    [Fact]
    public void FindAvailableName_accepts_the_last_probe_slot()
    {
        // Base plus (2)..(999) = 999 taken names, so the 1000th probe is free.
        Directory.CreateDirectory(Path.Combine(_root, BaseName));
        for (var suffix = 2; suffix < NewFolderProbe.MaxProbe; suffix++)
        {
            Directory.CreateDirectory(Taken(suffix));
        }

        var candidate = NewFolderProbe.FindAvailableName(_root);

        Assert.Equal(Taken(NewFolderProbe.MaxProbe), candidate);
    }

    [Fact]
    public void FindAvailableName_throws_when_all_probe_slots_are_taken()
    {
        Directory.CreateDirectory(Path.Combine(_root, BaseName));
        for (var suffix = 2; suffix <= NewFolderProbe.MaxProbe; suffix++)
        {
            Directory.CreateDirectory(Taken(suffix));
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => NewFolderProbe.FindAvailableName(_root));

        Assert.Contains("手动命名", ex.Message);
    }

    [Fact]
    public void CreateNewFolder_creates_the_probed_directory()
    {
        var created = NewFolderProbe.CreateNewFolder(_root);

        Assert.Equal(Path.Combine(_root, BaseName), created);
        Assert.True(Directory.Exists(created));
    }
}
