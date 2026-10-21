using System.Diagnostics;
using System.IO;
using System.Text;

namespace FileTabs;

/// <summary>
/// Opt-in startup milestones (FILETABS_STARTUP_TRACE=1). Off by default so the
/// measurement itself does not become part of the startup cost.
/// </summary>
internal static class StartupTiming
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("FILETABS_STARTUP_TRACE") == "1";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly StringBuilder Buffer = new();
    private static readonly object Gate = new();

    public static void Mark(string name)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            Buffer.AppendLine($"{Clock.Elapsed.TotalMilliseconds,8:F1} ms  {name}");
        }
    }

    public static void Flush()
    {
        if (!Enabled) return;
        lock (Gate)
        {
            if (Buffer.Length == 0) return;
            try
            {
                Directory.CreateDirectory(AppStorage.DataDirectory);
                File.AppendAllText(
                    Path.Combine(AppStorage.DataDirectory, "startup-trace.log"),
                    Buffer.ToString(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Buffer.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tracing must never affect startup.
            }
        }
    }
}
