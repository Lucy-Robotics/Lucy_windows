using System.Windows;
using Lucy.Views;

namespace Lucy.Tests;

internal static class InstallerDriver
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(110);

    public static InstallViewModel Install(E2EContext ctx, ShellViewModel shell, Window win, string target)
    {
        shell.ShowInstaller(InstallMode.Install, target);
        var install = (InstallViewModel)shell.Page!;
        var branch = Environment.GetEnvironmentVariable("LUCY_INSTALL_E2E_BRANCH");
        if (!string.IsNullOrEmpty(branch)) install.SelectedBranch = branch;
        var repos = Environment.GetEnvironmentVariable("LUCY_INSTALL_E2E_REPOS");
        install.ReviewConfig = !string.IsNullOrEmpty(repos);
        win.Show();
        ctx.Shot(win, "1-installer-setup.png");
        Assert.True(install.InstallCommand.CanExecute(null), install.PathError ?? "Install is disabled");

        install.InstallCommand.Execute(null);
        var started = DateTime.Now;
        var progressSaved = false;
        ctx.WaitUntil(() =>
        {
            if (install.ConfigEditor is { } editor)
            {
                editor.Documents[0].Text = File.ReadAllText(repos!);
                ctx.Shot(win, "2-installer-config.png");
                editor.SaveCommand.Execute(null);
            }
            if (!progressSaved && DateTime.Now - started > TimeSpan.FromMinutes(3))
            {
                ctx.Shot(win, "3-installer-progress.png");
                progressSaved = true;
            }
            return install.Phase is InstallPhase.Done or InstallPhase.Failed;
        }, InstallTimeout, "the installer to finish", () => SaveLog(ctx, install));
        SaveLog(ctx, install);
        ctx.Shot(win, "4-installer-done.png");
        ctx.Note($"installer: {install.Phase} after {(DateTime.Now - started).TotalMinutes:0} min");
        foreach (var s in install.Steps) ctx.Note($"  {s.Title}: {s.State} {s.Detail}");
        Assert.True(install.Phase == InstallPhase.Done, $"{install.Error}\n{Tail(install.Log.AllText(), 60)}");
        return install;
    }

    private static void SaveLog(E2EContext ctx, InstallViewModel install)
    {
        install.Log.Flush();
        File.WriteAllText(ctx.ShotPath("install.log"), install.Log.AllText());
    }

    private static string Tail(string text, int lines) =>
        string.Join("\n", text.Split('\n').TakeLast(lines));
}
