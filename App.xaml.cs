using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
// WinForms (UseWindowsForms) and WPF both define Application; WPF wins here.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace FileTabs;

public partial class App : Application
{
    private static readonly string InstanceSuffix =
        Environment.GetEnvironmentVariable("FILETABS_INSTANCE_KEY") is { Length: > 0 } key
            ? "." + key
            : string.Empty;

    private static readonly string MutexName = @"Local\FileTabs.SingleInstance" + InstanceSuffix;
    private static readonly string RestoreEventName = @"Local\FileTabs.RestoreSignal" + InstanceSuffix;
    private static readonly string PipeName = "FileTabs.SingleInstance.Pipe" + InstanceSuffix;

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _restoreSignal;
    private CancellationTokenSource? _pipeCts;
    private DropPointManager? _dropPoints;
    private bool _handlingUiException;

    /// <summary>
    /// Shared manager for the drag-and-drop staging pad. Settings dialog uses
    /// it to apply the on/off switch without restarting the app.
    /// </summary>
    public DropPointManager DropPoints => _dropPoints ??= new DropPointManager();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance election. createdNew alone is not enough: after the
        // first instance is killed, the named mutex can linger unnamed-owner-
        // less for a moment, so createdNew is false while nobody actually
        // holds it. WaitOne (with no wait) is the real "is someone holding it"
        // question; AbandonedMutexException means the previous owner died
        // without releasing, which means we take over.
        _singleInstanceMutex = new Mutex(initiallyOwned: false, MutexName);
        bool isNewInstance;
        try
        {
            isNewInstance = _singleInstanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The holder died without releasing; the mutex is ours now.
            isNewInstance = true;
        }
        _ownsSingleInstanceMutex = isNewInstance;
        if (!isNewInstance)
        {
            // Ask the running instance to come to the front, open the path
            // passed on our command line, then leave.
            //
            // This instance exists only to notify. Nothing in here may throw:
            // an unhandled exception would surface as 0xe0434352 and the user
            // would see a crash dialog for what should be a silent no-op.
            // UnauthorizedAccessException is the one that actually bites: when
            // the running instance is exiting, Local\ named objects can still
            // exist while their ACL denies access.
            try
            {
                _restoreSignal = EventWaitHandle.OpenExisting(RestoreEventName);
                _restoreSignal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other process may be closing; launching again is enough.
            }
            catch (UnauthorizedAccessException)
            {
                // The running instance's objects are being torn down and the
                // handle cannot be opened. Nothing to recover: exit quietly.
            }
            catch (ObjectDisposedException)
            {
                // Opened, then disposed by the owner mid-set.
            }
            TrySendPathToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        _restoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, RestoreEventName);
        ThreadPool.UnsafeRegisterWaitForSingleObject(
            _restoreSignal,
            static (_, timedOut) =>
            {
                if (timedOut) return;
                Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (Current?.MainWindow is { } window)
                    {
                        // 静默启动的实例从未显示过，只 Activate 会激活一个
                        // 不可见的窗口。
                        if (!window.IsVisible) window.Show();
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
        StartupTiming.Mark("app-onstartup");

        // No StartupUri in App.xaml on purpose: only the first instance may
        // build a window. A second instance (the one the shell redirect spawns
        // for every folder open) would otherwise restore the session and then
        // save its own empty tab list over it on exit.
        var main = new MainWindow();
        this.MainWindow = main;
        StartupTiming.Mark("mainwindow-created");
        // 登录自启或显式 --silent 启动都留在后台：窗口照常构建、会话照常恢复，
        // 但不显示，直到有人要它（打开文件夹、Win+E 被接管、或再次启动 exe）。
        if (!IsSilentStartup(e.Args))
        {
            main.Show();
            StartupTiming.Mark("window-show-returned");
        }
        else
        {
            // 静默启动仍要窗口句柄：DropPoint 的全局热键走
            // PresentationSource.FromVisual，没有 HWND 就静默失败；托盘和二次
            // 启动的激活也需要它。Show 后立即隐藏，句柄留下、窗口不出现。
            main.ShowActivated = false;
            main.Show();
            main.Hide();
            StartupTiming.Mark("window-hidden");
        }

        // 托盘常驻：静默启动和手动启动都要有，否则窗口关掉后既找不回程序
        // 也退不出去。拖放中转只是它的一个附加功能。
        DropPoints.EnsurePersistentTray();
        StartupTiming.Mark("tray-ready");

        if (AppSettings.Current.DropPointEnabled)
        {
            DropPoints.Start(main);
        }
        StartupTiming.Mark("droppoint-started");

        // First instance: listen for paths from later invocations.
        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => PipeServerLoopAsync(_pipeCts.Token));
        StartupTiming.Mark("pipe-started");

        // Handle the path we ourselves were started with.
        if (e.Args.Length > 0)
        {
            string path = NormalizeShellPath(e.Args[0]);
            if (Directory.Exists(path))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (Current?.MainWindow is MainWindow main)
                    {
                        main.OpenPathInNewTab(path);
                    }
                });
            }
        }

        // Crash-report triage for a tool used as a primary file manager.
        DispatcherUnhandledException += (_, args) =>
        {
            // A second exception raised while the recovery dialog is open must
            // not start a nested dialog chain. Stop the process instead.
            if (_handlingUiException)
            {
                AppStorage.LogCrash(
                    "Second UI exception while recovering from a crash", args.Exception);
                args.Handled = true;
                Shutdown();
                return;
            }
            _handlingUiException = true;
            // The recovery handler owns the shutdown decision now; letting the
            // default handler terminate first would skip the dialog.
            args.Handled = true;
            HandleUiException(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            AppStorage.Log("Unhandled exception: " + (exception ?? args.ExceptionObject));
            AppStorage.LogCrash(
                $"Unhandled non-UI exception (terminating={args.IsTerminating})", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppStorage.Log("Unobserved task exception: " + args.Exception);
            AppStorage.LogCrash("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        MigrateLegacyAutostart();
        // Older builds also redirected the Folder base class, which broke
        // "show in folder" system-wide. Put those verbs back on startup.
        NativeRedirect.RestoreBaseClassRedirect();
    }

    /// <summary>本次启动是否要求后台静默起来。</summary>
    private static bool IsSilentStartup(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, StartupRegistration.SilentArgument, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 静默启动之前写的自启项没有参数，登录时仍会弹窗。就地改写一次。
    /// </summary>
    private static void MigrateLegacyAutostart()
    {
        if (!StartupRegistration.IsLegacyCommand()) return;
        StartupRegistration.Enable();
    }

    /// <summary>
    /// One unhandled UI exception is enough: save what we can, give the user a
    /// recovery choice, then always shut down. Resuming normal work after the
    /// dialog would leave the process in an unknown state.
    /// </summary>
    private void HandleUiException(Exception exception)
    {
        try
        {
            AppStorage.Log("Unhandled UI exception: " + exception);
            AppStorage.LogCrash("Unhandled UI exception", exception);

            if (MainWindow is MainWindow main)
            {
                main.FlushSessionForCrash();
            }

            var action = CrashRecoveryAction.Exit;
            try
            {
                var dialog = new CrashRecoveryWindow(exception);
                if (MainWindow is MainWindow { IsVisible: true } owner)
                {
                    dialog.Owner = owner;
                }
                dialog.ShowDialog();
                action = dialog.Action;
            }
            catch (Exception dialogException)
            {
                // If even the dialog cannot be created, still offer the one
                // decision that matters instead of disappearing silently.
                AppStorage.LogCrash("Failed to show crash recovery dialog", dialogException);
                var result = MessageBox.Show(
                    "FileTabs 遇到未处理的错误。是否重启并恢复上次会话？",
                    "FileTabs", MessageBoxButton.YesNo, MessageBoxImage.Error);
                if (result == MessageBoxResult.Yes)
                {
                    action = CrashRecoveryAction.Restart;
                }
            }

            if (action == CrashRecoveryAction.Restart)
            {
                RestartApplication();
            }
        }
        catch (Exception recoveryException)
        {
            // The crash path itself must never throw another exception.
            AppStorage.LogCrash("Crash recovery handler failed", recoveryException);
        }
        finally
        {
            Shutdown();
        }
    }

    public void RestartForDataRestore()
    {
        if (MainWindow is MainWindow main)
        {
            main.PrepareForDataRestore();
        }
        RestartApplication();
        Shutdown();
    }

    private void RestartApplication()
    {
        try
        {
            // Release first: otherwise the new process sees a live instance,
            // forwards its activation to this one, and exits immediately.
            ReleaseSingleInstanceMutex();
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                AppStorage.LogCrash("Cannot restart FileTabs: process path is unavailable.");
                return;
            }
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Restart is best-effort; the user still has the data directory.
            AppStorage.LogCrash("Failed to restart FileTabs", ex);
        }
    }

    private void ReleaseSingleInstanceMutex()
    {
        if (!_ownsSingleInstanceMutex) return;
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was already released or abandoned.
        }
        _ownsSingleInstanceMutex = false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dropPoints?.Dispose();
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        // Only the creating instance owns the mutex; releasing it from the
        // short-lived second instance would throw.
        if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Verbs redirected to this executable use the shell's
    /// "exe" "%1" shape. When %1 is a drive root ("C:\"),
    /// CommandLineToArgvW treats the closing \" as an escaped quote, so the
    /// process receives C:" (the backslash is consumed by the escape).
    /// Restore it; otherwise Directory.Exists rejects the path and the open
    /// request is silently dropped.
    /// </summary>
    private static string NormalizeShellPath(string raw)
    {
        var path = raw.Trim();
        if (path.Length >= 2 && path[^1] == '"')
        {
            path = path[..^1] + '\\';
        }
        return path;
    }

    private static void TrySendPathToRunningInstance(string[] args)
    {
        if (args.Length == 0) return;
        string path = NormalizeShellPath(args[0]);
        if (!Directory.Exists(path)) return;
        string message = IsLaunchedByOwnShellView()
            ? InstanceMessage.ForInPlaceNavigation(path)
            : InstanceMessage.ForNewTab(path);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(message);
        }
        catch (Exception)
        {
            // Best-effort: this instance only notifies. If the pipe is not up,
            // is closing, or access is denied, exiting quietly is correct —
            // letting anything escape would crash the short-lived process for
            // no user-visible benefit.
        }
    }

    /// <summary>
    /// The Explorer view hosted inside FileTabs activates a folder through the
    /// shell, and the registry redirect turns that into a new FileTabs process.
    /// When that happens the parent process is FileTabs itself: the user is
    /// browsing inside a tab, not asking for another one, so the request must
    /// navigate in place.
    /// </summary>
    private static bool IsLaunchedByOwnShellView()
    {
        int parentId = GetParentProcessId();
        if (parentId <= 0) return false;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var self = Process.GetCurrentProcess();
            return string.Equals(
                parent.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
            when (ex is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int GetParentProcessId()
    {
        using var self = Process.GetCurrentProcess();
        var info = new ProcessBasicInformation();
        int status = NtQueryInformationProcess(
            self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        return status == 0 ? (int)info.InheritedFromUniqueProcessId : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    private async Task PipeServerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                string? line = await reader.ReadLineAsync(token);
                if (InstanceMessage.TryParse(line, out var receivedPath, out var navigateInPlace))
                {
                    var path = NormalizeShellPath(receivedPath);
                    if (Directory.Exists(path))
                    {
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (Current?.MainWindow is MainWindow main)
                            {
                                if (navigateInPlace)
                                {
                                    main.NavigateActiveTabInPlace(path);
                                }
                                else
                                {
                                    main.OpenPathInNewTab(path);
                                }
                            }
                        });
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                // Client disconnected unexpectedly; keep looping.
            }
            finally
            {
                if (server is not null) await server.DisposeAsync();
            }
        }
    }
}

