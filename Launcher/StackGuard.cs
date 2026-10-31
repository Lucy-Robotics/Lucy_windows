using Lucy.Core;

namespace Lucy.Launcher;

/// <summary>
/// Single-stack guard and orphan sweep — Windows port of launcher/preflight.py and
/// launcher/process.py: find Lucy stack processes tied to this workspace that nobody owns.
/// </summary>
public static class StackGuard
{
    private static readonly string[] NodeMarkers =
    {
        "rosbridge_websocket", "lucy.launch.py", "rviz2", "robot_state_publisher", "ros2_control_node",
        "config_pipeline_node", "client_registry_node", "parameter_bridge", "control_supervisor_node",
        "joint_state_publisher", "joint_command_echo",
    };

    /// <summary>process.py _matches_orphan_signature + _in_lucy_workspace, for a Windows command line.</summary>
    public static bool IsLucyOrphan(ProcessSnapshot p, string workspaceRoot, int selfPid)
    {
        if (p.Id == selfPid) return false;
        var cmd = p.CommandLine;
        if (string.IsNullOrEmpty(cmd)) return false;
        if (cmd.Contains("launcher.py") || cmd.Contains("Lucy.py") || cmd.Contains("-m launcher")) return false;
        if (p.Name.Equals("Lucy", StringComparison.OrdinalIgnoreCase)) return false;

        var norm = cmd.Replace('\\', '/');
        var lower = norm.ToLowerInvariant();
        var signature = lower.Contains("gz sim")
                        || NodeMarkers.Any(m => norm.Contains(m))
                        || (norm.Contains("spawner") && norm.Contains("controller_manager"))
                        || lower.Contains("vite")
                        || lower.Contains("pixi_lucy_launch.py");
        if (!signature) return false;

        var root = workspaceRoot.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var exe = p.ExecutablePath.Replace('\\', '/').ToLowerInvariant();
        var inWorkspace = lower.Contains(root + "/") || exe.StartsWith(root + "/");
        if (!inWorkspace) return false;
        if (lower.Contains("vite")) return lower.Contains(root + "/src/lucy_control_panel");
        return true;
    }

    public static List<ProcessSnapshot> FindOrphans(Workspace ws, ISet<int>? owned = null)
    {
        var self = Environment.ProcessId;
        return ProcessInspector.SnapshotAll()
            .Where(p => owned == null || !owned.Contains(p.Id))
            .Where(p => IsLucyOrphan(p, ws.Root, self))
            .ToList();
    }

    /// <summary>cleanup → wait up to 10 s → cleanup again (process.py _finish_teardown).</summary>
    public static async Task<int> SweepAsync(Workspace ws, ISet<int>? owned = null)
    {
        var killed = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            var orphans = await Task.Run(() => FindOrphans(ws, owned));
            if (orphans.Count == 0) break;
            foreach (var o in orphans)
            {
                Log.Info($"orphan sweep: killing {o.Id} {o.Name}: {o.CommandLine}");
                ProcessInspector.KillTree(o.Id);
                killed++;
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && orphans.Any(o => ProcessInspector.IsAlive(o.Id)))
                await Task.Delay(250);
        }
        return killed;
    }

    /// <summary>Another launcher (pidfile) still alive?</summary>
    public static int? LiveLauncherPid(Workspace ws)
    {
        try
        {
            if (!File.Exists(ws.PidFile)) return null;
            if (!int.TryParse(File.ReadAllText(ws.PidFile).Trim(), out var pid)) return null;
            if (pid == Environment.ProcessId || !ProcessInspector.IsAlive(pid)) return null;
            var snap = ProcessInspector.Snapshot(pid);
            if (snap == null) return null;
            var isLauncher = snap.Name.Equals("Lucy", StringComparison.OrdinalIgnoreCase)
                             || snap.CommandLine.Contains("-m launcher") || snap.CommandLine.Contains("launcher.py");
            return isLauncher ? pid : null;
        }
        catch { return null; }
    }

    public static void WritePidFile(Workspace ws)
    {
        try { File.WriteAllText(ws.PidFile, Environment.ProcessId.ToString()); }
        catch (Exception ex) { Log.Warn($"pidfile: {ex.Message}"); }
    }

    /// <summary>process.py prune_ros_logs: drop ~/.ros/log entries older than 7 days.</summary>
    public static void PruneRosLogs()
    {
        try
        {
            var dir = Path.Combine(ShellEnv.UserProfile, ".ros", "log");
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.Now.AddDays(-LauncherConstants.RosLogRetentionDays);
            foreach (var d in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (d.Name == "latest" || d.LastWriteTime >= cutoff) continue;
                try
                {
                    if (d is DirectoryInfo di) di.Delete(true); else d.Delete();
                }
                catch { /* in use */ }
            }
        }
        catch (Exception ex) { Log.Warn($"ros log prune: {ex.Message}"); }
    }
}
