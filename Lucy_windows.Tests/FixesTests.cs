using Lucy.Core;

namespace Lucy.Tests;

public class LineSplitterTests
{
    private static List<string> Split(params string[] chunks)
    {
        var lines = new List<string>();
        var s = new LineSplitter(lines.Add);
        foreach (var c in chunks) s.Feed(c);
        s.Complete();
        return lines;
    }

    [Fact]
    public void ColconReprintedStderrIsKept()
    {
        // colcon re-prints captured Windows output: "\r\r\n" line endings. Used to give empty lines.
        Assert.Equal(new[] { "CMake Error at CMakeLists.txt:12:", "  missing package" },
            Split("CMake Error at CMakeLists.txt:12:\r\r\n  missing package\r\r\n"));
    }

    [Fact]
    public void NormalLineEndings() =>
        Assert.Equal(new[] { "a", "b", "c" }, Split("a\nb\r\nc"));

    [Fact]
    public void ProgressRedrawsKeepTheLatestEvenAcrossReads() =>
        // git writes one progress update per write(): the "\r" ends a chunk.
        Assert.Equal(new[] { "Receiving objects: 100% (719/719), done.", "next" },
            Split("Receiving objects:   0% (1/719)\r", "Receiving objects:  50% (360/719)\r", "Receiving objects: 100% (719/719), done.\n", "next\n"));

    [Fact]
    public void CrLfSplitAcrossReads() =>
        Assert.Equal(new[] { "one", "two" }, Split("one\r", "\ntwo\r", "\r", "\n"));

    [Fact]
    public void EmptyLinesStayEmpty() =>
        Assert.Equal(new[] { "a", "", "b" }, Split("a\r\n\r\nb\r\n"));
}

public class FirmwareToolchainTests
{
    [Fact]
    public void PathsMatchFirmwareCargoEnv()
    {
        var ws = new Workspace(@"C:\Lucy");
        Assert.Equal(@"C:\Lucy\.pixi\envs\default\cargo", FirmwareToolchain.CargoHome(ws));
        Assert.Equal(@"C:\Lucy\.pixi\envs\default\rustup", FirmwareToolchain.RustupHome(ws));
    }

    [Fact]
    public async Task HelperModePrintsToTheCapturedConsole()
    {
        // Lucy.exe is a GUI-subsystem app: its helper output must still reach the Update tab.
        var exe = ManagedProcess.CtrlCHelper;
        Assert.NotNull(exe);
        var missing = Path.Combine(Path.GetTempPath(), "lucy-no-such-ws-" + Guid.NewGuid().ToString("N"));
        var r = await ProcessRunner.RunAsync(exe!, new[] { FirmwareToolchain.HelperArgument, missing });
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("pixi environment", r.Output);
    }

    /// <summary>
    /// Opt-in, heavy (~100 MB, a few minutes): real rustup install into a throwaway workspace.
    ///   set LUCY_NET_HEAVY=1 && dotnet test --filter Rustup_Real
    /// </summary>
    [Fact]
    public async Task Rustup_RealInstallWithCorrectlyNamedInstaller()
    {
        if (Environment.GetEnvironmentVariable("LUCY_NET_HEAVY") != "1") return;
        var root = Directory.CreateTempSubdirectory("lucyrust").FullName;
        var ws = new Workspace(root);
        Directory.CreateDirectory(FirmwareToolchain.EnvPrefix(ws));
        var log = new List<string>();
        try
        {
            Assert.True(await FirmwareToolchain.InstallRustupAsync(ws, l => { lock (log) log.Add(l); }), string.Join("\n", log));
            Assert.True(FirmwareToolchain.HasRustup(ws));
            Assert.DoesNotContain(log, l => l.Contains("unknown proxy name"));
            Assert.True(File.Exists(Path.Combine(FirmwareToolchain.CargoHome(ws), "bin", "cargo.exe")));
        }
        finally { await Integration.DeleteTreeAsync(root); }
    }
}

public class ColconLogsTests
{
    [Fact]
    public void FailedPackageLogIsSurfaced()
    {
        var ws = new Workspace(Directory.CreateTempSubdirectory("lucycolcon").FullName);
        var dir = Path.Combine(ws.Root, "log", "latest_build", "lucy_ros2_control");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "stdout_stderr.log"), new[]
        {
            "-- Configuring done",
            @"C:\ws\src\lucy_ros2_control\src\hw.cpp(42): error C2065: 'M_PI': undeclared identifier",
            "ninja: build stopped: subcommand failed.",
        });
        const string output = "Starting >>> lucy_ros2_control\nFailed   <<< lucy_ros2_control [17.6s, exited with code 2]\nAborted  <<< lucy_msgs [34.5s]\n";
        Assert.Equal(new[] { "lucy_ros2_control" }, ColconLogs.FailedPackages(output));
        var details = ColconLogs.FailureDetails(ws, output);
        Assert.Contains(details, l => l.Contains("error C2065"));
        Assert.Contains(details, l => l.Contains("===== lucy_ros2_control"));
    }

    [Fact]
    public void NothingWhenNoFailure() =>
        Assert.Empty(ColconLogs.FailureDetails(new Workspace(Path.GetTempPath()), "Summary: 9 packages finished"));
}
