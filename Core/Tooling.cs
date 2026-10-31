using System.Text.RegularExpressions;

namespace Lucy.Core;

public sealed record ToolStatus(string Name, bool Found, string? Path, string? Version, string? Problem = null)
{
    public string Display => Found ? $"{Name} {Version}".Trim() : $"{Name} — {Problem ?? "not found"}";
}

/// <summary>Detect and install the host prerequisites: Git, Python 3, pixi, MSVC Build Tools.</summary>
public static class Tooling
{
    public static readonly Version MinPixi = new(0, 78, 0);
    public static readonly Version MinPython = new(3, 9);
    public const string MsvcComponent = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";

    private static readonly string[] WingetCommon =
        { "--exact", "--silent", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity" };

    // ---- detection ---------------------------------------------------------

    public static ToolStatus FindWinget()
    {
        var p = ShellEnv.Which("winget");
        return new ToolStatus("winget", p != null, p, null, p == null ? "missing — install \"App Installer\" from the Microsoft Store" : null);
    }

    public static async Task<ToolStatus> FindGitAsync(CancellationToken ct = default)
    {
        var p = ShellEnv.Which("git");
        if (p == null) return new ToolStatus("Git", false, null, null);
        var r = await ProcessRunner.CaptureAsync(p, new[] { "--version" }, timeout: TimeSpan.FromSeconds(20), ct: ct);
        var v = Regex.Match(r.Output, @"\d+\.\d+\.\d+").Value;
        return new ToolStatus("Git", r.Ok, p, v, r.Ok ? null : "not working");
    }

    public static async Task<ToolStatus> FindPythonAsync(CancellationToken ct = default)
    {
        var candidates = new List<(string exe, string[] pre)>();
        var py = ShellEnv.Which("py");
        if (py != null) candidates.Add((py, new[] { "-3" }));
        var python = ShellEnv.Which("python");
        // The Microsoft Store alias in WindowsApps opens the Store instead of running Python.
        if (python != null && !python.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            candidates.Add((python, Array.Empty<string>()));
        var progs = Path.Combine(ShellEnv.LocalAppData, "Programs", "Python");
        if (Directory.Exists(progs))
            foreach (var d in Directory.GetDirectories(progs, "Python3*").OrderByDescending(d => d))
                candidates.Add((Path.Combine(d, "python.exe"), Array.Empty<string>()));

        foreach (var (exe, pre) in candidates)
        {
            if (!File.Exists(exe)) continue;
            var r = await ProcessRunner.CaptureAsync(exe,
                pre.Concat(new[] { "-c", "import sys;print(sys.executable);print('%d.%d.%d' % sys.version_info[:3])" }),
                timeout: TimeSpan.FromSeconds(30), ct: ct);
            if (!r.Ok) continue;
            var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2 || !Version.TryParse(lines[^1], out var ver)) continue;
            if (ver < MinPython)
                return new ToolStatus("Python", false, lines[0], ver.ToString(), $"{ver} is too old (3.9+ needed)");
            return new ToolStatus("Python", true, lines[0], ver.ToString());
        }
        return new ToolStatus("Python", false, null, null);
    }

    public static string? PixiPath()
    {
        var p = ShellEnv.Which("pixi");
        if (p != null) return p;
        foreach (var dir in ShellEnv.ExtraToolDirs().Take(2))
        {
            var c = Path.Combine(dir, "pixi.exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static async Task<ToolStatus> FindPixiAsync(CancellationToken ct = default)
    {
        var p = PixiPath();
        if (p == null) return new ToolStatus("pixi", false, null, null);
        var r = await ProcessRunner.CaptureAsync(p, new[] { "--version" }, timeout: TimeSpan.FromSeconds(30), ct: ct);
        var m = Regex.Match(r.Output, @"(\d+)\.(\d+)\.(\d+)");
        if (!r.Ok || !m.Success) return new ToolStatus("pixi", false, p, null, "not working");
        var v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return v < MinPixi
            ? new ToolStatus("pixi", false, p, v.ToString(), $"{v} is older than {MinPixi}")
            : new ToolStatus("pixi", true, p, v.ToString());
    }

    public static string VswherePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");

    /// <summary>MSVC x64 toolchain, detected exactly like install.py (vswhere + vcvars64.bat).</summary>
    public static async Task<ToolStatus> FindMsvcAsync(CancellationToken ct = default)
    {
        if (!File.Exists(VswherePath)) return new ToolStatus("MSVC Build Tools", false, null, null);
        var r = await ProcessRunner.CaptureAsync(VswherePath,
            new[] { "-latest", "-products", "*", "-requires", MsvcComponent, "-property", "installationPath" },
            timeout: TimeSpan.FromSeconds(30), ct: ct);
        var root = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(root)) return new ToolStatus("MSVC Build Tools", false, null, null);
        var vcvars = Path.Combine(root, "VC", "Auxiliary", "Build", "vcvars64.bat");
        return File.Exists(vcvars)
            ? new ToolStatus("MSVC Build Tools", true, root, "x64")
            : new ToolStatus("MSVC Build Tools", false, root, null, "vcvars64.bat missing");
    }

    // ---- installation ------------------------------------------------------

    public static Task<RunResult> InstallGitAsync(Action<string> log, CancellationToken ct) =>
        Winget(new[] { "install", "--id", "Git.Git" }, log, ct);

    public static Task<RunResult> InstallPythonAsync(Action<string> log, CancellationToken ct) =>
        Winget(new[] { "install", "--id", "Python.Python.3.12", "--scope", "user" }, log, ct);

    /// <summary>Same command as install.py's pixi bootstrap on Windows.</summary>
    public static Task<RunResult> InstallPixiAsync(Action<string> log, CancellationToken ct) =>
        ProcessRunner.RunAsync("powershell.exe",
            new[] { "-NoProfile", "-ExecutionPolicy", "ByPass", "-Command", "irm -useb https://pixi.sh/install.ps1 | iex" },
            onLine: log, ct: ct);

    /// <summary>Same winget call as install.py ensure_msvc (~1.5 GB, raises a UAC prompt).</summary>
    public static Task<RunResult> InstallMsvcAsync(Action<string> log, CancellationToken ct) =>
        Winget(new[]
        {
            "install", "--id", "Microsoft.VisualStudio.2022.BuildTools",
            "--override", $"--quiet --wait --norestart --add {MsvcComponent} --includeRecommended",
        }, log, ct);

    private static Task<RunResult> Winget(IEnumerable<string> args, Action<string> log, CancellationToken ct)
    {
        var all = args.Concat(WingetCommon).ToList();
        log("> winget " + CommandLine.Join(all));
        return ProcessRunner.RunAsync("winget", all, onLine: log, ct: ct);
    }

    /// <summary>winget exits non-zero when the package is already installed; treat that as success.</summary>
    public static bool WingetSucceeded(RunResult r) =>
        r.Ok || r.ExitCode == unchecked((int)0x8A15002B) /* APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE */
             || r.Output.Contains("already installed", StringComparison.OrdinalIgnoreCase)
             || r.Output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase);
}
