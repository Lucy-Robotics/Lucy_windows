using Lucy.Core;

namespace Lucy.Tests;

public class ConfigOverrideTests
{
    private const string Repos = """
    { "repos": [ { "name": "a", "branch": "master", "url_https": "https://x/a.git", "url_ssh": "git@x:a.git" } ] }
    """;

    private static Workspace NewWorkspace()
    {
        var dir = Directory.CreateTempSubdirectory("lucycfg").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "repos.json"), Repos);
        File.WriteAllText(Path.Combine(dir, "config", "launcher_config.json"), TestData.LauncherConfig);
        return new Workspace(dir);
    }

    [Theory]
    [InlineData("{ \"repos\": [ ", "Invalid JSON, line 1")]
    [InlineData("[]", "JSON object")]
    [InlineData("{}", "Missing \"repos\"")]
    [InlineData("{ \"repos\": [] }", "empty")]
    [InlineData("{ \"repos\": [ { \"branch\": \"x\" } ] }", "no \"name\"")]
    [InlineData("{ \"repos\": [ { \"name\": \"a\" } ] }", "needs \"url_https\"")]
    [InlineData("{ \"repos\": [ { \"name\": \"a\", \"url\": \"u\" }, { \"name\": \"A\", \"url\": \"u\" } ] }", "twice")]
    [InlineData("{ \"repos\": [ { \"name\": \"a\", \"url\": \"u\", }, ] }", "Invalid JSON")] // trailing comma: Python rejects it too
    public void ReposErrorsAreExplained(string json, string expected) =>
        Assert.Contains(expected, ConfigOverride.Validate(ConfigKind.Repos, json));

    [Fact]
    public void SyntaxErrorReportsTheLine()
    {
        var err = ConfigOverride.Validate(ConfigKind.Repos, "{\n  \"repos\": [\n    { \"name\": \"a\" \"url\": \"u\" }\n  ]\n}");
        Assert.StartsWith("Invalid JSON, line 3", err);
    }

    [Theory]
    [InlineData("{ \"packages\": [ { \"id\": \"x\", \"name\": \"X\", \"type\": \"tool\" } ] }", "core")]
    [InlineData("{ \"packages\": [ { \"id\": \"core\", \"name\": \"C\", \"type\": \"engine\" } ] }", "\"type\" must be")]
    [InlineData("{ \"packages\": [ { \"id\": \"core\", \"name\": \"C\", \"type\": \"core\", \"dependencies\": [\"nope\"] } ] }", "unknown package \"nope\"")]
    public void LauncherErrorsAreExplained(string json, string expected) =>
        Assert.Contains(expected, ConfigOverride.Validate(ConfigKind.Launcher, json));

    [Fact]
    public void ShippedFilesAreValid()
    {
        Assert.Null(ConfigOverride.Validate(ConfigKind.Launcher, TestData.LauncherConfig));
        const string cfg = @"C:\Dev\lucy_ws\config";
        if (!Directory.Exists(cfg)) return;
        foreach (var f in new[] { "repos.json", "repos.json.local.example" })
            Assert.Null(ConfigOverride.Validate(ConfigKind.Repos, File.ReadAllText(Path.Combine(cfg, f))));
        foreach (var f in new[] { "launcher_config.json", "launcher_config.json.local.example" })
            Assert.Null(ConfigOverride.Validate(ConfigKind.Launcher, File.ReadAllText(Path.Combine(cfg, f))));
    }

    [Fact]
    public void StartsFromTheTrackedFileThenFromTheOverride()
    {
        var o = new ConfigOverride(NewWorkspace(), ConfigKind.Repos);
        Assert.False(o.HasLocal);
        Assert.Equal(Repos, o.ReadCurrent());
        File.WriteAllText(o.LocalPath, "{\"repos\":[{\"name\":\"b\",\"url\":\"u\"}]}");
        Assert.Contains("\"b\"", o.ReadCurrent());
    }

    [Fact]
    public void SaveWritesOnlyRealChangesWithoutBom()
    {
        var o = new ConfigOverride(NewWorkspace(), ConfigKind.Repos);

        // Same content as the tracked file (even reformatted): no override.
        Assert.Equal(ConfigOverride.SaveResult.Unchanged, o.Save(ConfigOverride.Format(Repos)));
        Assert.False(o.HasLocal);

        var fork = Repos.Replace("https://x/a.git", "https://github.com/me/a.git").Replace("master", "my-branch");
        Assert.Equal(ConfigOverride.SaveResult.Written, o.Save(fork));
        var bytes = File.ReadAllBytes(o.LocalPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no UTF-8 BOM (Python json.load)");
        Assert.Equal(ConfigOverride.SaveResult.Unchanged, o.Save(fork));

        // Back to the defaults: the override is removed so install.py uses repos.json again.
        Assert.Equal(ConfigOverride.SaveResult.Removed, o.Save(Repos));
        Assert.False(o.HasLocal);
    }

    [Fact]
    public void InvalidContentIsNeverWritten()
    {
        var o = new ConfigOverride(NewWorkspace(), ConfigKind.Launcher);
        Assert.Throws<InvalidOperationException>(() => o.Save("{ broken"));
        Assert.False(o.HasLocal);
    }

    [Fact]
    public void LauncherOverrideIsPickedUpByTheLoader()
    {
        var ws = NewWorkspace();
        var o = new ConfigOverride(ws, ConfigKind.Launcher);
        o.Save("""{ "packages": [ { "id": "core", "name": "Core", "type": "core" }, { "id": "extra", "name": "Extra", "type": "tool" } ] }""");
        Assert.Equal(new[] { "core", "extra" }, Lucy.Launcher.LauncherConfigLoader.Load(ws).Select(p => p.Id));
    }

    [Fact]
    public void FormatReindentsAndKeepsInvalidTextAsIs()
    {
        Assert.Contains("\n  \"repos\"", ConfigOverride.Format("{\"repos\":[]}"));
        Assert.Equal("{ broken", ConfigOverride.Format("{ broken"));
    }
}
