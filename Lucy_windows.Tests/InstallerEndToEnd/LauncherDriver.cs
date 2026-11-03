using System.Windows;
using Lucy.Launcher;
using Lucy.Views;

namespace Lucy.Tests;

internal static class LauncherDriver
{
    private static readonly TimeSpan StackTimeout = TimeSpan.FromMinutes(8);

    public static LauncherViewModel OpenLauncher(E2EContext ctx, ShellViewModel shell, InstallViewModel install)
    {
        install.OpenLauncherCommand.Execute(null);
        var launcher = (LauncherViewModel)shell.Page!;
        ctx.WaitUntil(() => launcher.Loaded || launcher.HasLoadError, TimeSpan.FromMinutes(1), "the launcher to load");
        Assert.False(launcher.HasLoadError, launcher.LoadError);
        return launcher;
    }

    public static void StartCoreAndPanel(E2EContext ctx, LauncherViewModel launcher, Window win)
    {
        var state = launcher.Engine.State;
        var core = state.Get("core")!;
        var panel = state.Get("control_panel")!;
        ctx.WaitUntil(() => core.Selected && panel.Selected, TimeSpan.FromSeconds(10), "the launcher's auto-start", throwOnTimeout: false);
        if (!core.Selected || !panel.Selected)
        {
            core.Selected = true;
            panel.Selected = true;
            state.DefaultRobotSelection();
            launcher.ApplyCommand.Execute(null);
        }
        ctx.WaitUntil(() => (core.Status == PackageStatus.Running && panel.Status == PackageStatus.Running)
                            || core.Status == PackageStatus.Crashed || panel.Status == PackageStatus.Crashed,
            StackTimeout, "core and the control panel to run");
        ctx.Shot(win, "5-launcher-running.png");
        ctx.Note($"launcher: core={core.Status}, control_panel={panel.Status} at {launcher.ControlPanelUrl}");
        foreach (var s in launcher.Sessions) File.WriteAllText(ctx.ShotPath($"session-{s.Id}.log"), s.Log.AllText());
        Assert.Equal(PackageStatus.Running, core.Status);
        Assert.Equal(PackageStatus.Running, panel.Status);
    }

    public static void StopAll(E2EContext ctx, LauncherViewModel launcher)
    {
        var stop = launcher.Engine.StopAllAsync();
        ctx.WaitUntil(() => stop.IsCompleted, TimeSpan.FromMinutes(2), "the stack to stop", throwOnTimeout: false);
        launcher.Engine.FinishTeardown();
        var orphans = StackGuard.FindOrphans(launcher.Workspace);
        foreach (var o in orphans) ctx.Note($"orphan {o.Id} {o.CommandLine}");
    }
}
