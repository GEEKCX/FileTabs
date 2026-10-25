using System;
using System.Threading;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
// WinForms (UseWindowsForms) and WPF both define Application; WPF wins here.
using Application = System.Windows.Application;

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
        }

        // 托盘常驻：静默启动和手动启动都要有，否则窗口关掉后既找不回程序
        // 也退不出去。拖放中转只是它的一个附加功能。
        DropPoints.EnsurePersistentTray();

        if (AppSettings.Current.DropPointEnabled)
        {
            DropPoints.Start(main);
        }

        // First instance: listen for paths from later invocations.
        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => PipeServerLoopAsync(_pipeCts.Token));

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

        MigrateLegacyAutostart();
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
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(path);
        }
        catch (Exception)
        {
            // Best-effort: this instance only notifies. If the pipe is not up,
            // is closing, or access is denied, exiting quietly is correct —
            // letting anything escape would crash the short-lived process for
            // no user-visible benefit.
        }
    }

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
                if (!string.IsNullOrEmpty(line))
                {
                    var path = NormalizeShellPath(line);
                    if (Directory.Exists(path))
                    {
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (Current?.MainWindow is MainWindow main)
                            {
                                main.OpenPathInNewTab(path);
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

