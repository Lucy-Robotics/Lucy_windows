using System.Windows.Threading;
using Lucy.Core;
using Lucy.Launcher;
using Xunit.Abstractions;

namespace Lucy.Tests;

/// <summary>
/// Drives the real launcher engine against a built workspace. Opt-in:
///   set LUCY_E2E=C:\path\to\lucy_ws   then   dotnet test --filter EndToEnd
/// </summary>
public class EndToEndTests
{
    private readonly ITestOutputHelper _out;
    public EndToEndTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EndToEnd_CoreAndControlPanel_StartReadyAndStopClean()
    {
        var root = Environment.GetEnvironmentVariable("LUCY_E2E");
        if (string.IsNullOrEmpty(root)) return;
        var ws = new Workspace(root);
        Assert.True(ws.IsInstalled, "workspace must be built");

        RunOnDispatcher(async () =>
        {
            var engine = new LauncherEngine(ws);
            engine.Message += (m, _) => _out.WriteLine("message: " + m);
            engine.Load();
            _out.WriteLine("ports: " + engine.Ports);
            foreach (var p in engine.State.Packages) _out.WriteLine($"pkg {p.Id} supported={p.IsSupported} selected={p.Selected}");

            engine.State.Get("core")!.Selected = true;
            engine.State.Get("control_panel")!.Selected = true;
            engine.State.DefaultRobotSelection();
            await engine.ApplyAsync();

            var deadline = DateTime.Now.AddSeconds(240);
            string Snapshot() => string.Join(", ", engine.State.Packages.Where(p => p.Selected)
                .Select(p => $"{p.Id}={p.Status}{(p.StageText.Length > 0 ? " (" + p.StageText + ")" : "")}"));
            var last = "";
            while (DateTime.Now < deadline)
            {
                await Task.Delay(1000);
                var s = Snapshot();
                if (s != last) { _out.WriteLine($"{DateTime.Now:HH:mm:ss} {s}"); last = s; }
                var core = engine.State.Get("core")!;
                var lcp = engine.State.Get("control_panel")!;
                if (core.Status is PackageStatus.Crashed || lcp.Status is PackageStatus.Crashed) break;
                if (core.Status == PackageStatus.Running && lcp.Status == PackageStatus.Running) break;
            }

            foreach (var s in engine.Sessions)
            {
                _out.WriteLine($"---- {s.Title}: {s.Command}");
                foreach (var line in s.Log.AllText().Split('\n').TakeLast(25)) _out.WriteLine("   " + line.TrimEnd());
            }

            var coreStatus = engine.State.Get("core")!.Status;
            var lcpStatus = engine.State.Get("control_panel")!.Status;
            var owned = engine.Sessions.Where(s => s.Process != null).SelectMany(s => s.Process!.GetTreeProcessIds()).ToList();
            _out.WriteLine($"owned processes before stop: {owned.Count}");

            await engine.StopAllAsync();
            engine.FinishTeardown();

            var leftovers = owned.Where(ProcessInspector.IsAlive).ToList();
            var orphans = StackGuard.FindOrphans(ws);
            _out.WriteLine($"left alive: {leftovers.Count}, orphans: {orphans.Count}");
            foreach (var o in orphans) _out.WriteLine($"  orphan {o.Id} {o.CommandLine}");

            Assert.Empty(leftovers);
            Assert.Empty(orphans);
            Assert.False(File.Exists(ws.PidFile));
            Assert.Equal(PackageStatus.Running, lcpStatus);
            Assert.Equal(PackageStatus.Running, coreStatus);
        });
    }

    private static void RunOnDispatcher(Func<Task> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            d.BeginInvoke(async () =>
            {
                try { await body(); }
                catch (Exception ex) { error = ex; }
                finally { d.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
