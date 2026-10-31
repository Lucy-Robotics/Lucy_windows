using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Lucy.Core;

namespace Lucy.Launcher;

/// <summary>Inputs one readiness evaluation may need. Process snapshots are taken lazily, once.</summary>
public sealed class ProbeContext
{
    private readonly Func<List<ProcessSnapshot>> _snapshot;
    private List<ProcessSnapshot>? _processes;

    public ProbeContext(IDictionary<string, string> env, string workspaceRoot, Func<List<ProcessSnapshot>> processes)
    {
        Env = env;
        WorkspaceRoot = workspaceRoot;
        _snapshot = processes;
    }

    public IDictionary<string, string> Env { get; }
    public string WorkspaceRoot { get; }
    public List<ProcessSnapshot> Processes => _processes ??= _snapshot();
}

/// <summary>
/// Evaluates launcher_config.json readiness checks (POSIX shell one-liners) natively on Windows.
///   bash scripts/port_open.sh N       → TCP connect to localhost:N
///   pgrep -f 'pat' / pgrep -x name    → match the package's own process tree
///   bash scripts/controllers_active.sh → passes: controller_manager does not run on win-64
///                                        (pixi_lucy_launch.py starts a joint-state fallback)
///   test -f /tmp/...                  → passes (Linux-only marker files)
/// Anything else unknown passes, so it never blocks a package that is otherwise up.
/// </summary>
public static class ReadinessProbe
{
    private static readonly Regex Redirect = new(@"\s*(?:\d?>>?\s*/dev/null|\d?>&\d|&>\s*/dev/null)", RegexOptions.Compiled);
    private static readonly Regex CharClass = new(@"\[(.)\]", RegexOptions.Compiled);
    private static readonly Regex LeadingPath = new(@"^(?:[\w.\-]*/)+", RegexOptions.Compiled);

    public static bool Evaluate(string? check, ProbeContext ctx)
    {
        if (string.IsNullOrWhiteSpace(check)) return true;
        var expanded = ShellEnv.Expand(check, ctx.Env);
        foreach (var raw in expanded.Split("&&"))
        {
            var part = Redirect.Replace(raw, "").Trim();
            if (part.Length == 0) continue;
            if (!EvaluateSimple(part, ctx)) return false;
        }
        return true;
    }

    private static bool EvaluateSimple(string part, ProbeContext ctx)
    {
        var words = CommandLine.Tokenize(part);
        if (words.Count == 0) return true;
        var negate = false;
        if (words[0] == "!") { negate = true; words.RemoveAt(0); }
        var result = EvaluateWords(words, ctx);
        return negate ? !result : result;
    }

    private static bool EvaluateWords(List<string> w, ProbeContext ctx)
    {
        // bash scripts/<name>.sh args
        if (w[0] is "bash" or "sh" && w.Count >= 2)
        {
            var script = Path.GetFileName(w[1]);
            switch (script)
            {
                case "port_open.sh":
                    return w.Count >= 3 && int.TryParse(w[2], out var port) && IsPortOpen(port);
                case "controllers_active.sh":
                    return true;
                default:
                    return true;
            }
        }
        if (w[0] == "pgrep") return Pgrep(w.Skip(1).ToList(), ctx);
        if (w[0] == "test" && w.Count >= 3 && w[1] is "-f" or "-e" or "-d")
        {
            var path = w[2];
            if (path.StartsWith("/tmp/", StringComparison.Ordinal)) return true;
            var full = Path.IsPathRooted(path) ? path : Path.Combine(ctx.WorkspaceRoot, path);
            return w[1] == "-d" ? Directory.Exists(full) : File.Exists(full) || Directory.Exists(full);
        }
        if (w[0] == "true") return true;
        if (w[0] == "false") return false;
        if (w[0] == "win-port" && w.Count >= 2 && int.TryParse(w[1], out var p2)) return IsPortOpen(p2);
        if (w[0] == "win-proc" && w.Count >= 2) return ctx.Processes.Any(p => NormalizeCmd(p.CommandLine).Contains(w[1].ToLowerInvariant()));
        return true;
    }

    private static bool Pgrep(List<string> args, ProbeContext ctx)
    {
        var full = false;
        var exact = false;
        string? pattern = null;
        foreach (var a in args)
        {
            if (a.StartsWith('-') && pattern == null)
            {
                if (a.Contains('f')) full = true;
                if (a.Contains('x')) exact = true;
                continue;
            }
            pattern = a;
        }
        if (string.IsNullOrEmpty(pattern)) return false;
        var needle = CharClass.Replace(pattern, "$1");

        if (!full)
        {
            return ctx.Processes.Any(p => exact
                ? string.Equals(p.Name, needle, StringComparison.OrdinalIgnoreCase)
                : p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        needle = LeadingPath.Replace(needle.Trim(), "").ToLowerInvariant().Replace('\\', '/');
        return ctx.Processes.Any(p => NormalizeCmd(p.CommandLine).Contains(needle));
    }

    /// <summary>Lowercase, forward slashes, no quotes / .exe / -script.py, so POSIX patterns match.</summary>
    public static string NormalizeCmd(string cmd)
    {
        var s = cmd.ToLowerInvariant().Replace('\\', '/').Replace("\"", "");
        s = Regex.Replace(s, @"(?:\.exe|\.bat|\.cmd|-script\.py)(?=\s|$)", "");
        return Regex.Replace(s, @"\s+", " ");
    }

    public static bool IsPortOpen(int port, int timeoutMs = 300)
    {
        foreach (var addr in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                using var sock = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                var task = sock.ConnectAsync(new IPEndPoint(addr, port));
                if (task.Wait(timeoutMs) && sock.Connected) return true;
            }
            catch { /* refused / unsupported family */ }
        }
        return false;
    }
}
