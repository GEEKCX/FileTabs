using System;

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
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--install-redirect":
                    NativeRedirect.Install();
                    return 0;
                case "--uninstall-redirect":
                    NativeRedirect.Uninstall();
                    return 0;
            }
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
