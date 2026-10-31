using Lucy.Core;

namespace Lucy.Views;

/// <summary>Developer-mode SSH check shared by the installer and the launcher's Update.</summary>
public static class GitAccessPrompt
{
    /// <summary>
    /// Returns the extra environment for install.py: empty when SSH works (or is not used), the
    /// HTTPS rewrite when the user picks it, or null when the user cancels.
    /// </summary>
    public static async Task<Dictionary<string, string>?> EnsureAsync(Workspace ws, Action<string> log, CancellationToken ct = default)
    {
        var urls = GitAccess.SshUrls(ws);
        if (urls.Count == 0) return new();
        var git = ShellEnv.Which("git");
        if (git == null) return new(); // install.py reports it

        while (true)
        {
            log($"Checking SSH access for developer mode ({urls[0]})...");
            var probe = await GitAccess.ProbeSshAsync(git, urls[0], ct);
            ct.ThrowIfCancellationRequested();
            if (probe.Ok)
            {
                log("  SSH access OK.");
                return new();
            }
            log($"  SSH access failed: {probe.Reason}.");

            var choice = Dialogs.Choose("SSH access to the repositories",
                "Developer mode clones the repositories over SSH, but SSH access does not work on this account.",
                $"Reason: {probe.Reason}.\n\n" +
                "• Clone over HTTPS: works without a key. The repositories keep their SSH remotes, so pushing works once you add a key.\n" +
                "• Retry: after adding an SSH key to your Git host (ssh-keygen -t ed25519, then add the .pub key on GitHub).",
                "Clone over HTTPS", "Retry", "Cancel");
            switch (choice)
            {
                case 0:
                    log("  Using HTTPS for this run (SSH remotes are kept).");
                    return GitAccess.HttpsRewriteEnv(urls);
                case 1:
                    continue;
                default:
                    return null;
            }
        }
    }
}
