using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using FileTabs;

namespace FileTabs.Tests;

public sealed class ShellFileOperationsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"filetabs-shell-{Guid.NewGuid():N}");

    public ShellFileOperationsTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void RenameChangesCaseAndUndoRestoresIt()
    {
        var source = Path.Combine(_root, "abc.txt");
        File.WriteAllText(source, "x");

        RunSta(() => ShellFileOperations.Rename(source, "ABC.txt"));
        Assert.Contains("ABC.txt", Directory.GetFiles(_root).Select(Path.GetFileName));

        var renamed = Path.Combine(_root, "ABC.txt");
        RunSta(() => ShellFileOperations.Rename(renamed, "abc.txt"));
        Assert.Contains("abc.txt", Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    /// <summary>IFileOperation needs an STA thread; xUnit workers are MTA.</summary>
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("Shell rename did not finish in 15 seconds.");
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
