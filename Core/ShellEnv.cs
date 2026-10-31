using System.Collections;
using System.Text.RegularExpressions;

namespace Lucy.Core;

/// <summary>
/// Builds the environment for child processes. PATH is re-read from the registry each time,
/// so tools installed by winget / pixi's install.ps1 during this session are found without
/// restarting the app.
/// </summary>
public static class ShellEnv
{
    // Variables that would make pixi/conda think it is already inside an activated env.
    private static readonly string[] Scrub =
    {
        "CONDA_PREFIX", "CONDA_DEFAULT_ENV", "CONDA_SHLVL", "CONDA_PROMPT_MODIFIER",
        "PIXI_PROJECT_ROOT", "PIXI_PROJECT_NAME", "PIXI_PROJECT_MANIFEST", "PIXI_ENVIRONMENT_NAME",
        "PIXI_IN_SHELL", "PIXI_EXE", "PYTHONHOME", "PYTHONPATH", "VIRTUAL_ENV",
    };

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static IEnumerable<string> ExtraToolDirs()
    {
        yield return Path.Combine(UserProfile, ".pixi", "bin");
        yield return Path.Combine(LocalAppData, "pixi", "bin");
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, "Git", "cmd");
        yield return Path.Combine(LocalAppData, "Programs", "Git", "cmd");
        yield return Path.Combine(LocalAppData, "Microsoft", "WindowsApps"); // winget alias
    }

    public static string BuildPath()
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var p in raw.Split(';'))
            {
                var e = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
                if (e.Length == 0) continue;
                if (seen.Add(e.TrimEnd('\\'))) parts.Add(e);
            }
        }

        foreach (var d in ExtraToolDirs().Take(2))
            if (Directory.Exists(d)) Add(d);
        Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process));
        foreach (var d in ExtraToolDirs().Skip(2))
            if (Directory.Exists(d)) Add(d);
        return string.Join(";", parts);
    }

    /// <summary>Current environment + fresh PATH + overrides (later wins).</summary>
    public static Dictionary<string, string> Create(IDictionary<string, string>? overrides = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
            env[(string)kv.Key] = (string?)kv.Value ?? "";
        foreach (var k in Scrub) env.Remove(k);
        env["PATH"] = BuildPath();
        env["PYTHONUNBUFFERED"] = "1";
        env["PYTHONIOENCODING"] = "utf-8";
        env["PYTHONUTF8"] = "1";
        env["NO_COLOR"] = "1";
        env["FORCE_COLOR"] = "0";
        env["PIXI_COLOR"] = "never";
        if (overrides != null)
            foreach (var kv in overrides) env[kv.Key] = kv.Value;
        return env;
    }

    /// <summary>Find an executable on the given PATH (PATHEXT order), or null.</summary>
    public static string? Which(string name, IDictionary<string, string>? env = null)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var path = env != null && env.TryGetValue("PATH", out var p) ? p : BuildPath();
        var exts = Path.HasExtension(name) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat", ".com" };
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* bad PATH entry */ }
            }
        }
        return null;
    }

    private static readonly Regex VarRef = new(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::-(?<def>[^}]*))?\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>POSIX-style ${VAR}, ${VAR:-default}, $VAR expansion. Unset vars stay unexpanded.</summary>
    public static string Expand(string text, IDictionary<string, string> env)
    {
        return VarRef.Replace(text, m =>
        {
            var name = m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["bare"].Value;
            if (env.TryGetValue(name, out var v) && v.Length > 0) return v;
            if (m.Groups["def"].Success) return m.Groups["def"].Value;
            return m.Value;
        });
    }
}
