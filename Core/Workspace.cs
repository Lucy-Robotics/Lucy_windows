namespace Lucy.Core;

/// <summary>A lucy_ws checkout on disk.</summary>
public sealed class Workspace
{
    public Workspace(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    public string EnvFile => Path.Combine(Root, ".env");
    public string ConfigDir => Path.Combine(Root, "config");
    public string LauncherConfig => Path.Combine(ConfigDir, "launcher_config.json");
    public string LauncherConfigLocal => Path.Combine(ConfigDir, "launcher_config.json.local");
    public string ModifiersFile => Path.Combine(Root, ".lucy_launcher_modifiers.json");
    public string SelectionFile => Path.Combine(Root, ".lucy_launcher_state.json");
    public string PidFile => Path.Combine(Root, ".lucy_launcher.pid");
    public string InstallProfile => Path.Combine(ConfigDir, "install.profile.json");
    public string ControlPanelDir => Path.Combine(Root, "src", "lucy_control_panel");

    /// <summary>A lucy_ws clone (pixi.toml + install.py).</summary>
    public bool IsCheckout => File.Exists(Path.Combine(Root, "pixi.toml")) && File.Exists(Path.Combine(Root, "install.py"));

    /// <summary>Built: same check as Lucy.py is_installed().</summary>
    public bool IsInstalled =>
        File.Exists(Path.Combine(Root, "install", "setup.bat")) || File.Exists(Path.Combine(Root, "install", "setup.bash"));

    /// <summary>
    /// package.py _ros_pkg_installed. Windows builds use colcon --merge-install, where packages
    /// land in install/share/&lt;pkg&gt; instead of install/&lt;pkg&gt;: accept both layouts.
    /// </summary>
    public bool IsRosPackageInstalled(string pkg) =>
        Directory.Exists(Path.Combine(Root, "install", pkg)) || Directory.Exists(Path.Combine(Root, "install", "share", pkg));

    public bool DevMode => DotEnv.GetDevMode(EnvFile);

    /// <summary>Child environment: fresh PATH + workspace .env (overrides, as launcher/config.py does).</summary>
    public Dictionary<string, string> CreateEnvironment(IDictionary<string, string>? extra = null)
    {
        var env = ShellEnv.Create(DotEnv.Read(EnvFile));
        if (extra != null)
            foreach (var kv in extra) env[kv.Key] = kv.Value;
        return env;
    }

    public static string? ValidateLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Choose an install folder.";
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return "This is not a valid folder path."; }
        if (full.Contains(' '))
            return "The path must not contain spaces (colcon and the MSVC toolchain do not support them).";
        if (full.Any(c => c > 127))
            return "Use a path with plain ASCII characters only.";
        if (full.Length > 60)
            return "Choose a shorter path (60 characters max) — ROS builds hit Windows path limits.";
        if (Path.GetPathRoot(full) == full)
            return "Choose a folder, not the root of a drive.";
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (full.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
            return "Program Files needs admin rights; choose a folder you own (e.g. C:\\Lucy).";
        return null;
    }
}
