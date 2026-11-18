using System;
using System.IO;

namespace FileTabs;

/// <summary>
/// Entry point. The registry switches are handled before any WPF object
/// exists: App.xaml declares StartupUri, so constructing the Application and
/// calling Shutdown() during startup still creates the main window, which then
/// overwrites the saved session with an empty tab list.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupTiming.Mark("process-main");
        // WPF's default adorner selection paints over the glyphs, so
        // SelectionTextBrush is ignored and an opaque highlight hides the text.
        AppContext.SetSwitch(
            "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering",
            false);

        if (args.Length > 0)
        {
            // The redirect verbs accept --dry-run to preview without writing;
            // detect it before the switch so verb and flag can appear in
            // either order (Program.cs has no WPF object yet, so printing to
            // the console here is safe).
            bool redirectDryRun =
                args.Contains("--dry-run", StringComparer.Ordinal);
            switch (args[0])
            {
                case "--install-redirect":
                    if (redirectDryRun)
                    {
                        EnsureConsole();
                        NativeRedirect.PreviewInstall();
                    }
                    else
                    {
                        NativeRedirect.Install();
                    }
                    return 0;
                case "--uninstall-redirect":
                    if (redirectDryRun)
                    {
                        EnsureConsole();
                        NativeRedirect.PreviewUninstall();
                    }
                    else
                    {
                        NativeRedirect.Uninstall();
                    }
                    return 0;
                // Benchmarks the filesystem paths that make the UI stall. It
                // runs before WPF exists and returns without a message loop.
                case "--bench":
                    return BenchCommand.Run(args);
            }
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    // A WinExe has no console of its own. Attach to the launching shell so
    // the --* --dry-run previews can print to stdout (mirrors the approach
    // BenchCommand.EnsureConsole uses for --bench); with output redirected to
    // a file it still lands there because the process inherits that handle.
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static void EnsureConsole()
    {
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                if (!AttachConsole(AttachParentProcess)) { /* no console; fine */ }
            }
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch (IOException)
        {
            // No usable console; output is simply unavailable.
        }
    }
}
