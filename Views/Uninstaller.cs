using Lucy.Core;
using Lucy.Launcher;

namespace Lucy.Views;

/// <summary>Uninstall flow shared by <c>Lucy.exe --uninstall</c> (Apps &amp; features) and the launcher button.</summary>
public static class Uninstaller
{
    /// <param name="workspace">Workspace path from settings, if any.</param>
    /// <param name="stopStack">Stops what this Lucy instance runs (launcher), before anything is removed.</param>
    /// <returns>True when Lucy was removed: the caller must exit right away.</returns>
    public static async Task<bool> RunAsync(string? workspace, Func<Task>? stopStack = null)
    {
        var canDeleteWs = workspace != null && Integration.IsSafeToDelete(workspace);
        string detail;
        if (workspace == null)
            detail = "This removes the app, its Start Menu shortcut and settings.";
        else if (canDeleteWs)
            detail = $"This removes the app, its Start Menu shortcut and settings.\n\n\"Remove all\" also deletes the workspace {workspace} (sources, build, pixi environment, several GB). That deletion runs in the background after Lucy closes and can take a few minutes.";
        else
            detail = $"This removes the app, its Start Menu shortcut and settings. The workspace {workspace} is kept.";

        int choice;
        if (canDeleteWs)
        {
            choice = Dialogs.ChooseDanger("Uninstall Lucy", "Remove Lucy from this computer?", detail, 1,
                "Remove app only", "Remove all", "Cancel");
            if (choice is < 0 or 2) return false;
        }
        else
        {
            if (!Dialogs.Confirm("Uninstall Lucy", "Remove Lucy from this computer?", "Uninstall", "Cancel", danger: true, detail: detail))
                return false;
            choice = 0;
        }

        try
        {
            if (stopStack != null) await stopStack();
            // Never leave a stack running behind, even one started by the TUI or another session.
            if (workspace != null)
                foreach (var o in StackGuard.FindOrphans(new Workspace(workspace))) ProcessInspector.KillTree(o.Id);

            Integration.Uninstall(choice == 1 ? workspace : null);
            Log.Info($"uninstalled ({(choice == 1 ? "app + workspace" : "app only")})");
            if (choice == 1)
                Dialogs.Info("Uninstall Lucy", "Lucy has been removed.",
                    $"The folder {workspace} is now being deleted in the background. It holds several GB, so this can take a few minutes after this window closes. You can keep using your computer meanwhile.");
            else
                Dialogs.Info("Uninstall Lucy", "Lucy has been removed.",
                    workspace != null ? $"The workspace {workspace} was kept." : null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("uninstall failed", ex);
            Dialogs.Error("Uninstall Lucy", "Uninstall failed: " + ex.Message);
            return false;
        }
    }
}
