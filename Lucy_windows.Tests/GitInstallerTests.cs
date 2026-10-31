using Lucy.Core;

namespace Lucy.Tests;

public class GitInstallerTests
{
    private const string Release = """
    {
      "tag_name": "v2.51.0.windows.1",
      "body": "| File | SHA-256 |\n| --- | --- |\n| MinGit-2.51.0-64-bit.zip | ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789 |\n",
      "assets": [
        { "name": "Git-2.51.0-64-bit.exe", "browser_download_url": "https://x/Git.exe", "size": 70000000 },
        { "name": "MinGit-2.51.0-32-bit.zip", "browser_download_url": "https://x/32.zip", "size": 1 },
        { "name": "MinGit-2.51.0-busybox-64-bit.zip", "browser_download_url": "https://x/bb.zip", "size": 1 },
        { "name": "MinGit-2.51.0-arm64.zip", "browser_download_url": "https://x/arm.zip", "size": 1 },
        { "name": "MinGit-2.51.0-64-bit.zip", "browser_download_url": "https://x/MinGit.zip", "size": 40000000 }
      ]
    }
    """;

    [Fact]
    public void PicksThePlain64BitMinGitAndItsHashFromTheNotes()
    {
        var a = GitInstaller.PickAsset(Release)!;
        Assert.Equal("MinGit-2.51.0-64-bit.zip", a.Name);
        Assert.Equal("https://x/MinGit.zip", a.Url);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", a.Sha256);
    }

    [Fact]
    public void AssetDigestWinsOverTheNotes()
    {
        var json = Release.Replace("\"size\": 40000000 }", "\"size\": 40000000, \"digest\": \"sha256:1111111111111111111111111111111111111111111111111111111111111111\" }");
        Assert.Equal(new string('1', 64), GitInstaller.PickAsset(json)!.Sha256);
    }

    [Fact]
    public void NoMinGitMeansNull() =>
        Assert.Null(GitInstaller.PickAsset("""{ "body": "", "assets": [ { "name": "Git-2.51.0-64-bit.exe", "browser_download_url": "u", "size": 1 } ] }"""));

    /// <summary>
    /// Opt-in (network, ~40 MB): real GitHub release, hash check, extraction into a temp folder.
    /// Never touches the user PATH or %LOCALAPPDATA%.   set LUCY_NET=1 && dotnet test --filter MinGit_Real
    /// </summary>
    [Fact]
    public async Task MinGit_RealDownloadVerifiesAndRuns()
    {
        if (Environment.GetEnvironmentVariable("LUCY_NET") != "1") return;
        var dir = Path.Combine(Directory.CreateTempSubdirectory("lucymingit").FullName, "MinGit");
        var log = new List<string>();
        var r = await GitInstaller.InstallMinGitAsync(l => log.Add(l), CancellationToken.None, dir, updateUserPath: false);
        Assert.True(r.Ok, string.Join("\n", log));
        Assert.Contains(log, l => l.Contains("SHA-256 verified"));
        var git = Path.Combine(dir, "cmd", "git.exe");
        var v = await ProcessRunner.CaptureAsync(git, new[] { "--version" });
        Assert.StartsWith("git version", v.Output.Trim());
        // install.py clones over HTTPS: make sure this git can talk to GitHub.
        var ls = await ProcessRunner.CaptureAsync(git, new[] { "ls-remote", "--heads", "https://github.com/Lucy-Robotics/lucy_ws.git" }, timeout: TimeSpan.FromSeconds(60));
        Assert.True(ls.Ok, ls.Output);
        Directory.Delete(Path.GetDirectoryName(dir)!, true);
    }
}
