using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Lucy.Core;

/// <summary>Start Menu shortcut, "Apps &amp; features" entry, self-copy and uninstall.</summary>
public static class Integration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lucy";

    /// <summary>True for the published single-file Lucy.exe (dev builds have Lucy.dll beside them).</summary>
    public static bool IsSingleFile => !File.Exists(Path.Combine(AppContext.BaseDirectory, "Lucy.dll"));

    public static string Version =>
        typeof(Integration).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Copy the running exe to %LOCALAPPDATA%\Programs\Lucy and return the path to launch from.</summary>
    public static string InstallSelf()
    {
        if (!IsSingleFile) return AppPaths.CurrentExe;
        var target = AppPaths.InstalledExe;
        if (string.Equals(Path.GetFullPath(AppPaths.CurrentExe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return target;
        Directory.CreateDirectory(AppPaths.InstalledExeDir);
        File.Copy(AppPaths.CurrentExe, target, overwrite: true);
        return target;
    }

    public static void CreateStartMenuShortcut(string exe, string workspace)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(AppPaths.StartMenuShortcut);
            lnk.TargetPath = exe;
            lnk.WorkingDirectory = workspace;
            lnk.IconLocation = exe + ",0";
            lnk.Description = "Lucy — install, update and launch the Lucy robotics stack";
            lnk.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    public static void RegisterUninstall(string exe, string workspace)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Lucy");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Lucy Robotics");
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("InstallLocation", workspace);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("URLInfoAbout", AppPaths.WebsiteUrl);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>config/install.profile.json — written by the previous installer too; kept for tooling.</summary>
    public static void WriteInstallProfile(Workspace ws, string branch, bool dev)
    {
        Directory.CreateDirectory(ws.ConfigDir);
        var profile = new Dictionary<string, object?>
        {
            ["installer"] = "Lucy.exe",
            ["installer_version"] = Version,
            ["workspace"] = ws.Root,
            ["branch"] = branch,
            ["developer"] = dev,
            ["installed_at"] = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(ws.InstallProfile, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Remove the shortcut, registry entry and app copy. The workspace is only deleted on request.</summary>
    public static void Uninstall(string? workspaceToDelete)
    {
        try { File.Delete(AppPaths.StartMenuShortcut); } catch (Exception ex) { Log.Warn(ex.Message); }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch (Exception ex) { Log.Warn(ex.Message); }
        try { File.Delete(AppPaths.SettingsFile); } catch (Exception ex) { Log.Warn(ex.Message); }

        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(workspaceToDelete) && IsSafeToDelete(workspaceToDelete))
            dirs.Add(Path.GetFullPath(workspaceToDelete));
        if (Directory.Exists(AppPaths.InstalledExeDir))
            dirs.Add(AppPaths.InstalledExeDir);
        if (dirs.Count == 0) return;

        // The running exe cannot delete itself (nor a folder it runs from): a detached script waits
        // for this process to exit, removes the folders, then deletes itself.
        var pid = System.Environment.ProcessId;
        var script = Path.Combine(Path.GetTempPath(), $"lucy-uninstall-{pid}.cmd");
        File.WriteAllLines(script, CleanupScript(pid, dirs));
        Log.Info($"uninstall: removing {string.Join(", ", dirs)} after exit");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"\"{script}\"\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    /// <summary>Batch script: wait for <paramref name="pid"/> to exit, remove <paramref name="dirs"/>, delete itself.</summary>
    internal static List<string> CleanupScript(int pid, IEnumerable<string> dirs)
    {
        var lines = new List<string>
        {
            "@echo off",
            ":wait",
            // Full System32 paths: a PATH containing Git's usr\bin would otherwise pick up Unix find.
            $@"""%SystemRoot%\System32\tasklist.exe"" /FI ""PID eq {pid}"" /NH 2>nul | ""%SystemRoot%\System32\find.exe"" "" {pid} "" >nul && (""%SystemRoot%\System32\PING.EXE"" -n 2 127.0.0.1 >nul & goto wait)",
        };
        lines.AddRange(dirs.Select(d => $"rmdir /s /q \"{d}\""));
        lines.Add("(goto) 2>nul & del \"%~f0\"");
        return lines;
    }

    /// <summary>Only ever delete something that is really a lucy_ws checkout, never a drive root.</summary>
    internal static bool IsSafeToDelete(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(full)) return false;
            var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? "");
            if (root.Equals(full, StringComparison.OrdinalIgnoreCase)) return false;
            return new Workspace(full).IsCheckout;
        }
        catch { return false; }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"could not open {url}: {ex.Message}"); }
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"could not open {path}: {ex.Message}"); }
    }
}
