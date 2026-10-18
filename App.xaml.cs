using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
// WinForms (UseWindowsForms) and WPF both define Application; WPF wins here.
using Application = System.Windows.Application;

namespace FileTabs;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _restoreSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\FileTabs.SingleInstance", out var isNewInstance);
        _ownsSingleInstanceMutex = isNewInstance;
        if (!isNewInstance)
        {
            // Ask the running instance to come to the front, then leave.
            try
            {
                _restoreSignal = EventWaitHandle.OpenExisting(@"Local\FileTabs.RestoreSignal");
                _restoreSignal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other process may be closing; launching again is enough.
            }
            Shutdown();
            return;
        }

        _restoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FileTabs.RestoreSignal");
        ThreadPool.UnsafeRegisterWaitForSingleObject(
            _restoreSignal,
            static (_, timedOut) =>
            {
                if (timedOut) return;
                Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (Current?.MainWindow is { } window)
                    {
                        if (window.WindowState == WindowState.Minimized)
                        {
                            window.WindowState = WindowState.Normal;
                        }
                        window.Activate();
                    }
                });
            },
            null,
            -1,
            false);

        base.OnStartup(e);

        // Crash-report triage for a tool used as a primary file manager.
        DispatcherUnhandledException += (_, args) =>
        {
            AppStorage.Log("Unhandled UI exception: " + args.Exception);
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppStorage.Log("Unhandled exception: " + ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppStorage.Log("Unobserved task exception: " + args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only the creating instance owns the mutex; releasing it from the
        // short-lived second instance would throw.
        if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

