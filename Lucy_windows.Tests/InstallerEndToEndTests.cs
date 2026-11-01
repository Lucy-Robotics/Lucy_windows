using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Lucy.Core;
using Lucy.Launcher;
using Lucy.Views;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Lucy.Tests;

public class InstallerEndToEndTests
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(110);
    private static readonly TimeSpan StackTimeout = TimeSpan.FromMinutes(8);
    private const double MovedRad = 0.05;

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
            try { Run(target, shots); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private void Run(string target, string shots)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var dict in new[] { "Theme/Tokens.xaml", "Theme/Controls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Lucy;component/{dict}") });
        DialogWindow.AutoAnswer = (title, buttons) =>
        {
            Note($"dialog \"{title}\": {buttons.FirstOrDefault()}");
            return 0;
        };

        var shell = new ShellViewModel();
        var win = new MainWindow(shell) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        LauncherViewModel? launcher = null;
        try
        {
            shell.ShowInstaller(InstallMode.Install, target);
            var install = (InstallViewModel)shell.Page!;
            var branch = Environment.GetEnvironmentVariable("LUCY_INSTALL_E2E_BRANCH");
            if (!string.IsNullOrEmpty(branch)) install.SelectedBranch = branch;
            var repos = Environment.GetEnvironmentVariable("LUCY_INSTALL_E2E_REPOS");
            install.ReviewConfig = !string.IsNullOrEmpty(repos);
            win.Show();
            RenderTests.Save(win, Path.Combine(shots, "1-installer-setup.png"));
            Assert.True(install.InstallCommand.CanExecute(null), install.PathError ?? "Install is disabled");

            install.InstallCommand.Execute(null);
            var started = DateTime.Now;
            var progressSaved = false;
            WaitUntil(() =>
            {
                if (install.ConfigEditor is { } editor)
                {
                    editor.Documents[0].Text = File.ReadAllText(repos!);
                    RenderTests.Save(win, Path.Combine(shots, "2-installer-config.png"));
                    editor.SaveCommand.Execute(null);
                }
                if (!progressSaved && DateTime.Now - started > TimeSpan.FromMinutes(3))
                {
                    RenderTests.Save(win, Path.Combine(shots, "3-installer-progress.png"));
                    progressSaved = true;
                }
                return install.Phase is InstallPhase.Done or InstallPhase.Failed;
            }, InstallTimeout, "the installer to finish", () => SaveLog(install, shots));
            SaveLog(install, shots);
            RenderTests.Save(win, Path.Combine(shots, "4-installer-done.png"));
            Note($"installer: {install.Phase} after {(DateTime.Now - started).TotalMinutes:0} min");
            foreach (var s in install.Steps) Note($"  {s.Title}: {s.State} {s.Detail}");
            Assert.True(install.Phase == InstallPhase.Done, $"{install.Error}\n{Tail(install.Log.AllText(), 60)}");

            install.OpenLauncherCommand.Execute(null);
            launcher = (LauncherViewModel)shell.Page!;
            WaitUntil(() => launcher.Loaded || launcher.HasLoadError, TimeSpan.FromMinutes(1), "the launcher to load");
            Assert.False(launcher.HasLoadError, launcher.LoadError);
            var state = launcher.Engine.State;
            var core = state.Get("core")!;
            var panel = state.Get("control_panel")!;
            WaitUntil(() => core.Selected && panel.Selected, TimeSpan.FromSeconds(10), "the launcher's auto-start", throwOnTimeout: false);
            if (!core.Selected || !panel.Selected)
            {
                core.Selected = true;
                panel.Selected = true;
                state.DefaultRobotSelection();
                launcher.ApplyCommand.Execute(null);
            }
            WaitUntil(() => (core.Status == PackageStatus.Running && panel.Status == PackageStatus.Running)
                            || core.Status == PackageStatus.Crashed || panel.Status == PackageStatus.Crashed,
                StackTimeout, "core and the control panel to run");
            RenderTests.Save(win, Path.Combine(shots, "5-launcher-running.png"));
            Note($"launcher: core={core.Status}, control_panel={panel.Status} at {launcher.ControlPanelUrl}");
            foreach (var s in launcher.Sessions) File.WriteAllText(Path.Combine(shots, $"session-{s.Id}.log"), s.Log.AllText());
            Assert.Equal(PackageStatus.Running, core.Status);
            Assert.Equal(PackageStatus.Running, panel.Status);

            var drive = Task.Run(() => DriveControlPanel(launcher.ControlPanelUrl, shots));
            WaitUntil(() => drive.IsCompleted, TimeSpan.FromMinutes(10), "the control panel checks");
            drive.GetAwaiter().GetResult();
        }
        finally
        {
            if (launcher != null)
            {
                var stop = launcher.Engine.StopAllAsync();
                WaitUntil(() => stop.IsCompleted, TimeSpan.FromMinutes(2), "the stack to stop", throwOnTimeout: false);
                launcher.Engine.FinishTeardown();
                var orphans = StackGuard.FindOrphans(launcher.Workspace);
                foreach (var o in orphans) Note($"orphan {o.Id} {o.CommandLine}");
            }
            DialogWindow.AutoAnswer = null;
            win.ForceClose();
            RenderTests.Pump(TimeSpan.FromMilliseconds(300));
            app.Shutdown();
        }
    }

    private async Task DriveControlPanel(string url, string shots)
    {
        Assert.False(string.IsNullOrEmpty(url), "the launcher reports no control panel URL");
        using var joints = await JointStates.ConnectAsync(new Uri("ws://127.0.0.1:9090"));
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1600, Height = 1000 } });
        page.Console += (_, m) => { if (m.Type == "error") Note("browser: " + m.Text); };
        await page.AddInitScriptAsync("localStorage.setItem('gettingStartedCompleted', 'true')");
        try
        {
            await page.GotoAsync(url);
            await page.Locator(".ant-slider").First.WaitForAsync(new() { Timeout = 180_000 });
            Note("control panel shows the joint sliders");

            await page.GetByRole(AriaRole.Button, new() { Name = "Control Robot: ON", Exact = true }).First.ClickAsync();
            var sliders = page.Locator(".ant-slider:not(.ant-slider-disabled)");
            await sliders.First.WaitForAsync(new() { Timeout = 30_000 });
            Note("Control Robot is ON");

            var start = await joints.WaitAsync("/joint_states", s => s.Count > 0, TimeSpan.FromSeconds(30));
            var rail = sliders.First;
            await rail.ScrollIntoViewIfNeededAsync();
            var box = (await rail.BoundingBoxAsync())!;
            var moved = false;
            foreach (var fraction in new[] { 0.8f, 0.2f })
            {
                await page.Mouse.ClickAsync(box.X + box.Width * fraction, box.Y + box.Height / 2);
                try
                {
                    await joints.WaitAsync("the slider's move", s => Diff(start, s) > MovedRad, TimeSpan.FromSeconds(20));
                    moved = true;
                    break;
                }
                catch (TimeoutException) { Note($"no move after clicking the slider at {fraction:P0}"); }
            }
            Assert.True(moved, "dragging a slider with Control Robot ON never moved /joint_states");
            Note("a slider moved the robot");
        }
        finally
        {
            await page.Mouse.MoveAsync(0, 0);
            await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "6-control-panel.png"), FullPage = true });
        }
    }

    private static double Diff(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b) =>
        a.Keys.Intersect(b.Keys).Select(k => Math.Abs(a[k] - b[k])).DefaultIfEmpty(0).Max();

    private void WaitUntil(Func<bool> done, TimeSpan timeout, string what, Action? onTimeout = null, bool throwOnTimeout = true)
    {
        var deadline = DateTime.Now + timeout;
        while (!done())
        {
            if (DateTime.Now > deadline)
            {
                onTimeout?.Invoke();
                if (throwOnTimeout) throw new TimeoutException($"timed out after {timeout.TotalMinutes:0.#} min waiting for {what}");
                return;
            }
            RenderTests.Pump(TimeSpan.FromMilliseconds(500));
        }
    }

    private static void SaveLog(InstallViewModel install, string shots)
    {
        install.Log.Flush();
        File.WriteAllText(Path.Combine(shots, "install.log"), install.Log.AllText());
    }

    private static string Tail(string text, int lines) =>
        string.Join("\n", text.Split('\n').TakeLast(lines));

    private void Note(string line)
    {
        _out.WriteLine(line);
        Console.WriteLine("installer-e2e: " + line);
    }

    private sealed class JointStates : IDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly CancellationTokenSource _stop = new();
        private volatile IReadOnlyDictionary<string, double> _latest = new Dictionary<string, double>();

        public static async Task<JointStates> ConnectAsync(Uri rosbridge)
        {
            var j = new JointStates();
            await j._ws.ConnectAsync(rosbridge, CancellationToken.None);
            var subscribe = """{"op":"subscribe","topic":"/joint_states","type":"sensor_msgs/msg/JointState","throttle_rate":100}""";
            await j._ws.SendAsync(Encoding.UTF8.GetBytes(subscribe), WebSocketMessageType.Text, true, CancellationToken.None);
            _ = j.ReceiveAsync();
            return j;
        }

        private async Task ReceiveAsync()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (!_stop.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    var r = await _ws.ReceiveAsync(buffer, _stop.Token);
                    message.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    var json = message.ToArray();
                    message.SetLength(0);
                    try { Update(json); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }

        private void Update(byte[] json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("msg", out var msg)) return;
            var names = msg.GetProperty("name").EnumerateArray().Select(n => n.GetString()!).ToList();
            var positions = msg.GetProperty("position").EnumerateArray().Select(p => p.GetDouble()).ToList();
            var next = new Dictionary<string, double>(_latest);
            for (var i = 0; i < Math.Min(names.Count, positions.Count); i++) next[names[i]] = positions[i];
            _latest = next;
        }

        public async Task<IReadOnlyDictionary<string, double>> WaitAsync(string what, Func<IReadOnlyDictionary<string, double>, bool> ok, TimeSpan timeout)
        {
            var deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                var snapshot = _latest;
                if (ok(snapshot)) return snapshot;
                await Task.Delay(200);
            }
            throw new TimeoutException($"/joint_states never showed {what}");
        }

        public void Dispose()
        {
            _stop.Cancel();
            _ws.Dispose();
        }
    }
}
