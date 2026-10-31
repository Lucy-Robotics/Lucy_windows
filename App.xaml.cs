using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Lucy.Core;
using Lucy.Views;

namespace Lucy;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\Lucy.Windows.SingleInstance";
    private Mutex? _mutex;
    private ShellViewModel? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("unhandled", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("unobserved task", ex.Exception); ex.SetObserved(); };

        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        Log.Info($"Lucy {Integration.Version} starting ({string.Join(' ', e.Args)})");

        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            ActivateOtherInstance();
            Shutdown();
            return;
        }

        if (args.Contains("--uninstall"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // the dialogs must not end the app
            RunUninstall();
            Shutdown();
            return;
        }

        _shell = new ShellViewModel();
        var window = new MainWindow(_shell);
        MainWindow = window;

        var settings = AppSettings.Load();
        var ws = ResolveWorkspace(settings);
        if (args.Contains("--install") || ws == null)
            _shell.ShowInstaller(InstallMode.Install, ws?.Root);
        else if (!ws.IsInstalled)
            _shell.ShowInstaller(InstallMode.Resume, ws.Root);
        else
            _shell.ShowLauncher(ws);

        window.Show();
    }

    /// <summary>The configured workspace, or C:\Lucy / the exe's own folder when they hold a checkout.</summary>
    private static Workspace? ResolveWorkspace(AppSettings settings)
    {
        var candidates = new List<string?>
        {
            settings.Workspace,
            AppPaths.DefaultWorkspace,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
        };
        foreach (var c in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            try
            {
                var ws = new Workspace(c!);
                if (!ws.IsCheckout) continue;
                if (settings.Workspace != ws.Root)
                {
                    settings.Workspace = ws.Root;
                    settings.Save();
                }
                return ws;
            }
            catch { /* invalid path */ }
        }
        return null;
    }

    private static void ActivateOtherInstance()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            if (Native.IsIconic(p.MainWindowHandle)) Native.ShowWindow(p.MainWindowHandle, Native.SW_RESTORE);
            Native.SetForegroundWindow(p.MainWindowHandle);
            break;
        }
    }

    // No stack to stop here (no launcher open), so this completes synchronously.
    private static void RunUninstall() =>
        Uninstaller.RunAsync(AppSettings.Load().Workspace).GetAwaiter().GetResult();

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Logoff / shutdown: kill everything now (the job objects would do it anyway).
        Log.Info("session ending");
        _shell?.Page?.EmergencyShutdown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("exit");
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        Dialogs.Error("Unexpected error", e.Exception.Message, "Details were written to " + Log.Directory);
        e.Handled = true;
    }
}
