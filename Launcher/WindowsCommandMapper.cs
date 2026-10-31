using Lucy.Core;

namespace Lucy.Launcher;

/// <summary>What to run for a package on Windows.</summary>
public sealed record LaunchPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    ConsoleMode Mode,
    IReadOnlyDictionary<string, string> ExtraEnv,
    string Display,
    string? RawArguments = null);

/// <summary>
/// Translates launcher_config.json commands (written for bash + tmux) into Windows process
/// launches. lucy_ws is not modified: a `command_win` key, if ever added, wins.
/// </summary>
public static class WindowsCommandMapper
{
    public const string CoreLaunchScript = "scripts/pixi_lucy_launch.py";
    private static readonly string[] GazeboArgs = { "gazebo:=true", "gazebo:=1", "gazebo:=yes" };

    /// <summary>Reason a package cannot run on Windows, or null.</summary>
    public static string? UnsupportedReason(PackageConfig p)
    {
        if (p.WindowsSupported == false) return "Not supported on Windows";
        if (p.WindowsSupported == true) return null;
        if (p.IsModifier && GazeboArgs.Contains(p.Command.Trim().ToLowerInvariant()))
            return "Gazebo is not supported on Windows yet (gz_ros2_control crashes) — use the Control Panel 3D view";
        return null;
    }

    /// <summary>Packages that need an interactive terminal (a tmux window on Linux).</summary>
    public static bool NeedsTerminal(PackageConfig p) =>
        p.IsTool || !string.IsNullOrEmpty(p.NavHint) || p.Command.Contains("bash -i");

    /// <summary>Core + selected modifiers → scripts/pixi_lucy_launch.py (DLL-path fix, DDS scope, joint-state fallback).</summary>
    public static LaunchPlan ForCore(PackageConfig core, IEnumerable<PackageConfig> modifiers, string pixi)
    {
        if (!string.IsNullOrEmpty(core.CommandWin))
            return FromCommand(core.CommandWin!, modifiers.Select(m => m.CommandWin ?? m.Command), pixi, ConsoleMode.Captured);

        var env = new Dictionary<string, string>();
        var args = new List<string>();
        foreach (var m in modifiers)
        {
            foreach (var word in CommandLine.Tokenize(m.CommandWin ?? m.Command))
            {
                if (word.StartsWith("robot_package:=", StringComparison.Ordinal))
                    env["LUCY_ROBOT_PACKAGE"] = word["robot_package:=".Length..]; // the script prepends robot_package:=
                else
                    args.Add(word);
            }
        }
        // Extra words of core's own command after "ros2 launch lucy_bringup lucy.launch.py" are launch args too.
        var coreWords = CommandLine.Tokenize(core.Command);
        var launchIdx = coreWords.FindIndex(w => w.EndsWith(".launch.py", StringComparison.Ordinal));
        if (launchIdx >= 0) args.InsertRange(0, coreWords.Skip(launchIdx + 1));

        var pixiArgs = new List<string> { "run", "--", "python", CoreLaunchScript };
        pixiArgs.AddRange(args);
        var display = (env.TryGetValue("LUCY_ROBOT_PACKAGE", out var robot) ? $"LUCY_ROBOT_PACKAGE={robot} " : "")
                      + "pixi " + CommandLine.Join(pixiArgs);
        return new LaunchPlan(pixi, pixiArgs, ConsoleMode.Captured, env, display);
    }

    /// <summary>Interfaces and tools.</summary>
    public static LaunchPlan ForPackage(PackageConfig p, string pixi)
    {
        var mode = NeedsTerminal(p) ? ConsoleMode.NewWindow : ConsoleMode.Captured;
        if (!string.IsNullOrEmpty(p.CommandWin)) return FromCommand(p.CommandWin!, Array.Empty<string>(), pixi, mode);
        if (p.IsComplex) return FromCommand(p.ComplexStart!, Array.Empty<string>(), pixi, mode);

        // "Open Console": echo "...tmux hint..." && bash -i  →  a pixi shell in its own window.
        if (p.Command.Contains("bash -i"))
            return new LaunchPlan(pixi, new[] { "shell" }, ConsoleMode.NewWindow, new Dictionary<string, string>(), "pixi shell");

        var plan = FromCommand(p.Command, Array.Empty<string>(), pixi, mode);
        return p.IsTool && mode == ConsoleMode.NewWindow ? PauseOnExit(plan) : plan;
    }

    /// <summary>apply.py: tools end with "--- Process finished, press any key to close ---".</summary>
    private static LaunchPlan PauseOnExit(LaunchPlan plan)
    {
        var inner = CommandLine.Join(new[] { plan.FileName }.Concat(plan.Arguments));
        var script = $"{inner} & echo. & echo --- Process finished, press any key to close --- & pause >nul";
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return plan with { FileName = cmd, Arguments = Array.Empty<string>(), RawArguments = $"/d /s /c \"{script}\"" };
    }

    private static LaunchPlan FromCommand(string command, IEnumerable<string> extraArgs, string pixi, ConsoleMode mode)
    {
        var words = CommandLine.Tokenize(command);
        words.AddRange(extraArgs.SelectMany(CommandLine.Tokenize));
        var env = new Dictionary<string, string>();
        // Leading VAR=value assignments.
        while (words.Count > 0 && words[0].Contains('=') && !words[0].StartsWith('-') && char.IsLetter(words[0][0])
               && words[0].IndexOf('=') is var eq && words[0][..eq].All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            env[words[0][..eq]] = words[0][(eq + 1)..];
            words.RemoveAt(0);
        }
        if (words.Count == 0) throw new InvalidOperationException($"Empty command: '{command}'");

        List<string> args;
        if (words[0] == "pixi")
        {
            args = words.Skip(1).ToList(); // `pixi run panel-dev` runs as-is
        }
        else
        {
            // Plain commands run inside the pixi environment, as shell.py does (`pixi run -- <cmd>`).
            // Tasks wrapping bash scripts (pixi_gui_launch.sh) are bypassed by calling the tool directly.
            args = new List<string> { "run", "--" };
            args.AddRange(words);
        }
        return new LaunchPlan(pixi, args, mode, env, "pixi " + CommandLine.Join(args));
    }
}
