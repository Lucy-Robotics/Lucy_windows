using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lucy.Core;

/// <summary>
/// Developer mode makes install.py clone with the repos' <c>url_ssh</c>. On a machine without an SSH
/// key (or that never connected to the host) that fails with "Host key verification failed" /
/// "Permission denied (publickey)". This checks SSH access first and can rewrite the SSH URLs to
/// HTTPS for one process tree only, through git's GIT_CONFIG_* environment variables: nothing is
/// written to the user's git config, and the clones keep their SSH remotes for later pushes.
/// </summary>
public static class GitAccess
{
    private static readonly Regex ScpLike = new(@"^(?<user>[\w.-]+)@(?<host>[\w.-]+):(?<path>.+)$");
    private static readonly Regex SshScheme = new(@"^ssh://(?<user>[\w.-]+)@(?<host>[\w.-]+)(:\d+)?/(?<path>.+)$");

    /// <summary>The url_ssh entries of the repos config install.py will read (.local first).</summary>
    public static List<string> SshUrls(Workspace ws)
    {
        var text = new ConfigOverride(ws, ConfigKind.Repos).ReadCurrent();
        try
        {
            return (JsonNode.Parse(text)?["repos"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(r => r["url_ssh"]?.GetValue<string>()?.Trim())
                .Where(u => !string.IsNullOrEmpty(u) && (ScpLike.IsMatch(u) || SshScheme.IsMatch(u)))
                .Select(u => u!)
                .Distinct()
                .ToList();
        }
        catch { return new List<string>(); }
    }

    public sealed record ProbeResult(bool Ok, string Reason);

    /// <summary>
    /// <c>git ls-remote &lt;url&gt;</c> with the same git install.py uses, non-interactive. An unknown
    /// host key is accepted on first use (StrictHostKeyChecking=accept-new), like answering "yes".
    /// </summary>
    public static async Task<ProbeResult> ProbeSshAsync(string git, string url, CancellationToken ct = default)
    {
        var env = ShellEnv.Create(new Dictionary<string, string>
        {
            ["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15",
            ["GIT_TERMINAL_PROMPT"] = "0",
        });
        var r = await ProcessRunner.CaptureAsync(git, new[] { "ls-remote", "--heads", url }, env: env,
            timeout: TimeSpan.FromSeconds(45), ct: ct);
        if (r.Ok) return new ProbeResult(true, "");
        var o = r.Output;
        var reason =
            o.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? "no SSH key on this account is authorized on the server (Permission denied (publickey))"
            : o.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) ? "the server's host key could not be verified"
            : o.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase) ? "the server could not be reached (DNS)"
            : o.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? "the connection timed out (port 22 may be blocked)"
            : o.Contains("not found", StringComparison.OrdinalIgnoreCase) && o.Contains("ssh", StringComparison.OrdinalIgnoreCase) ? "no ssh client was found"
            : $"git exited with code {r.ExitCode}";
        return new ProbeResult(false, reason);
    }

    /// <summary>
    /// Environment making git use HTTPS for the given SSH URLs' hosts
    /// (<c>url.https://host/.insteadOf git@host:</c>), for the child process tree only.
    /// </summary>
    public static Dictionary<string, string> HttpsRewriteEnv(IEnumerable<string> sshUrls)
    {
        var rules = new List<(string Https, string Ssh)>();
        foreach (var u in sshUrls)
        {
            if (ScpLike.Match(u) is { Success: true } m)
                rules.Add(($"https://{m.Groups["host"].Value}/", $"{m.Groups["user"].Value}@{m.Groups["host"].Value}:"));
            else if (SshScheme.Match(u) is { Success: true } s)
                rules.Add(($"https://{s.Groups["host"].Value}/", u[..(u.IndexOf(s.Groups["path"].Value, StringComparison.Ordinal))]));
        }
        var env = new Dictionary<string, string>();
        var i = 0;
        foreach (var (https, ssh) in rules.Distinct())
        {
            env[$"GIT_CONFIG_KEY_{i}"] = $"url.{https}.insteadOf";
            env[$"GIT_CONFIG_VALUE_{i}"] = ssh;
            i++;
        }
        if (i > 0) env["GIT_CONFIG_COUNT"] = i.ToString();
        return env;
    }
}
