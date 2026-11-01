using System.Windows;
using System.Windows.Threading;
using Lucy.Launcher;
using Lucy.Views;
using Xunit.Abstractions;

namespace Lucy.Tests;

public class InstallerEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public InstallerEndToEndTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void InstallerEndToEnd_InstallsFromScratch_AndTheControlPanelMovesTheRobot()
    {
        var target = Environment.GetEnvironmentVariable("LUCY_INSTALL_E2E");
        if (string.IsNullOrEmpty(target)) return;
        var shots = Environment.GetEnvironmentVariable("LUCY_INSTALL_E2E_SHOTS") ?? target.TrimEnd('\\') + "-e2e";
        Directory.CreateDirectory(shots);

        Exception? error = null;
        var t = new Thread(() =>
        {
            try { Run(new E2EContext(_out, shots), target); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Run(E2EContext ctx, string target)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        foreach (var dict in new[] { "Theme/Tokens.xaml", "Theme/Controls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Lucy;component/{dict}") });
        DialogWindow.AutoAnswer = (title, buttons) =>
        {
            ctx.Note($"dialog \"{title}\": {buttons.FirstOrDefault()}");
            return 0;
        };

        var shell = new ShellViewModel();
        var win = new MainWindow(shell) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        LauncherViewModel? launcher = null;
        try
        {
            var install = InstallerDriver.Install(ctx, shell, win, target);
            launcher = LauncherDriver.OpenLauncher(ctx, shell, install);
            LauncherDriver.StartCoreAndPanel(ctx, launcher, win);

            var url = launcher.ControlPanelUrl;
            var drive = Task.Run(() => ControlPanelDriver.MoveTheRobotAsync(ctx, url));
            ctx.WaitUntil(() => drive.IsCompleted, TimeSpan.FromMinutes(10), "the control panel checks");
            drive.GetAwaiter().GetResult();
        }
        finally
        {
            if (launcher != null) LauncherDriver.StopAll(ctx, launcher);
            DialogWindow.AutoAnswer = null;
            win.ForceClose();
            RenderTests.Pump(TimeSpan.FromMilliseconds(300));
            app.Shutdown();
        }
    }
}
