using Lucy.Core;

namespace Lucy.Tests;

public class GitAccessTests
{
    [Fact]
    public void SshUrlsComeFromTheOverrideFirst()
    {
        var dir = Directory.CreateTempSubdirectory("lucygit").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "repos.json"),
            """{ "repos": [ { "name": "a", "url_https": "https://github.com/o/a.git", "url_ssh": "git@github.com:o/a.git" } ] }""");
        var ws = new Workspace(dir);
        Assert.Equal(new[] { "git@github.com:o/a.git" }, GitAccess.SshUrls(ws));

        File.WriteAllText(Path.Combine(dir, "config", "repos.json.local"), """
        { "repos": [
          { "name": "a", "url_ssh": "git@github.com:me/a.git" },
          { "name": "b", "url_ssh": "ssh://git@gitlab.example.com:2222/team/b.git" },
          { "name": "c", "url_https": "https://x/c.git" }
        ] }
        """);
        Assert.Equal(new[] { "git@github.com:me/a.git", "ssh://git@gitlab.example.com:2222/team/b.git" }, GitAccess.SshUrls(ws));
    }

    [Fact]
    public void RewriteEnvMapsEachHostToHttps()
    {
        var env = GitAccess.HttpsRewriteEnv(new[]
        {
            "git@github.com:o/a.git", "git@github.com:o/b.git", "ssh://git@gitlab.example.com:2222/team/b.git",
        });
        Assert.Equal("2", env["GIT_CONFIG_COUNT"]);
        Assert.Equal("url.https://github.com/.insteadOf", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("git@github.com:", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("url.https://gitlab.example.com/.insteadOf", env["GIT_CONFIG_KEY_1"]);
        Assert.Equal("ssh://git@gitlab.example.com:2222/", env["GIT_CONFIG_VALUE_1"]);
        Assert.Empty(GitAccess.HttpsRewriteEnv(Array.Empty<string>()));
    }

    /// <summary>Opt-in (network): set LUCY_NET=1.</summary>
    [Fact]
    public async Task Net_RewriteMakesSshUrlsWorkWithoutSsh()
    {
        if (Environment.GetEnvironmentVariable("LUCY_NET") != "1") return;
        var git = ShellEnv.Which("git")!;
        const string url = "git@github.com:Lucy-Robotics/lucy_ws.git";
        // An ssh that always fails: proves the clone below never uses SSH.
        var noSsh = new Dictionary<string, string> { ["GIT_SSH_COMMAND"] = "exit 1", ["GIT_TERMINAL_PROMPT"] = "0" };

        var without = await ProcessRunner.CaptureAsync(git, new[] { "ls-remote", "--heads", url }, env: ShellEnv.Create(noSsh));
        Assert.False(without.Ok);

        var with = new Dictionary<string, string>(noSsh);
        foreach (var kv in GitAccess.HttpsRewriteEnv(new[] { url })) with[kv.Key] = kv.Value;
        var r = await ProcessRunner.CaptureAsync(git, new[] { "ls-remote", "--heads", url }, env: ShellEnv.Create(with));
        Assert.True(r.Ok, r.Output);
        Assert.Contains("refs/heads/", r.Output);
    }

    [Fact]
    public async Task Net_ProbeExplainsUnreachableHosts()
    {
        if (Environment.GetEnvironmentVariable("LUCY_NET") != "1") return;
        var p = await GitAccess.ProbeSshAsync(ShellEnv.Which("git")!, "git@host.invalid:o/r.git");
        Assert.False(p.Ok);
        Assert.Contains("could not be reached", p.Reason);
    }
}

public class DeleteTreeTests
{
    [Fact]
    public async Task RemovesReadOnlyFilesAndDoesNotFollowJunctions()
    {
        var root = Directory.CreateTempSubdirectory("lucydel").FullName;
        var target = Path.Combine(root, "My Lucy");            // a space, like a real path
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(target, ".git", "objects", "pack"));
        Directory.CreateDirectory(outside);
        var pack = Path.Combine(target, ".git", "objects", "pack", "pack-1.pack");
        File.WriteAllText(pack, "x");
        File.SetAttributes(pack, FileAttributes.ReadOnly);       // git packs are read-only
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
        var mk = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            new[] { "/d", "/c", "mklink", "/J", Path.Combine(target, "link"), outside });
        Assert.True(mk.Ok, mk.Output);

        Assert.True(await Integration.DeleteTreeAsync(target));
        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")), "a junction target must never be deleted");
        Directory.Delete(root, true);
    }
}
