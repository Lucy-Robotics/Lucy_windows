using Lucy.Core;

namespace Lucy.Tests;

/// <summary>Integration tests of the job-object process layer against real Windows processes.</summary>
public class ProcessTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static string Ping => Path.Combine(Environment.SystemDirectory, "PING.EXE");

    [Fact]
    public async Task CapturesOutputAndExitCode()
    {
        var r = await ProcessRunner.RunAsync(Cmd, new[] { "/d", "/c", "echo hello& echo world 1>&2& exit 3" });
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("hello", r.Output);
        Assert.Contains("world", r.Output);
    }

    [Fact]
    public async Task PassesEnvironmentAndWorkingDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("lucycwd").FullName;
        var env = ShellEnv.Create(new Dictionary<string, string> { ["LUCY_TEST_VAR"] = "abc" });
        var r = await ProcessRunner.RunAsync(Cmd, new[] { "/d", "/c", "echo %LUCY_TEST_VAR% & cd" }, dir, env);
        Assert.Contains("abc", r.Output);
        Assert.Contains(Path.GetFileName(dir), r.Output);
    }

    [Fact]
    public async Task KillTerminatesTheWholeTree()
    {
        // cmd starts a detached grandchild (start /b) and then waits on its own ping.
        using var p = ManagedProcess.Start(new ProcessSpec
        {
            FileName = Cmd,
            RawArguments = $"/d /c \"start \"\" /b \"{Ping}\" -n 60 127.0.0.1 >nul & \"{Ping}\" -n 60 127.0.0.1 >nul\"",
        });
        await WaitUntil(() => p.GetTreeProcessIds().Length >= 3, TimeSpan.FromSeconds(10));
        var ids = p.GetTreeProcessIds();
        Assert.True(ids.Length >= 3, $"expected cmd + 2 pings, got {ids.Length}");

        p.Kill();
        await WaitUntil(() => !p.IsTreeAlive, TimeSpan.FromSeconds(10));
        Assert.False(p.IsTreeAlive);
        Assert.All(ids, id => Assert.False(ProcessInspector.IsAlive(id)));
        await p.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DisposeKillsTree()
    {
        int[] ids;
        using (var p = ManagedProcess.Start(new ProcessSpec { FileName = Ping, Arguments = new[] { "-n", "60", "127.0.0.1" } }))
        {
            await WaitUntil(() => p.GetTreeProcessIds().Length >= 1, TimeSpan.FromSeconds(5));
            ids = p.GetTreeProcessIds();
        }
        await WaitUntil(() => ids.All(id => !ProcessInspector.IsAlive(id)), TimeSpan.FromSeconds(10));
        Assert.All(ids, id => Assert.False(ProcessInspector.IsAlive(id)));
    }

    [Fact]
    public async Task CtrlCStopsGracefully()
    {
        // ping prints its statistics when interrupted with Ctrl+C: proof the signal arrived.
        var lines = new List<string>();
        using var p = ManagedProcess.Start(new ProcessSpec { FileName = Ping, Arguments = new[] { "-n", "60", "127.0.0.1" } },
            l => { lock (lines) lines.Add(l); });
        await WaitUntil(() => { lock (lines) return lines.Count >= 3; }, TimeSpan.FromSeconds(10));
        var started = DateTime.UtcNow;
        await p.StopAsync(TimeSpan.FromSeconds(10));
        Assert.False(p.IsTreeAlive);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "Ctrl+C should end ping well before the grace period");
        await p.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        lock (lines) Assert.Contains(lines, l => l.Contains("Ping statistics", StringComparison.OrdinalIgnoreCase) || l.Contains("Statistiques", StringComparison.OrdinalIgnoreCase) || l.Contains("Control-C") || l.Contains("^C"));
    }

    [Fact]
    public async Task CancellationKillsRunAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync(Ping, new[] { "-n", "60", "127.0.0.1" }, ct: cts.Token));
    }

    [Fact]
    public void CommandLineOfOwnProcessIsReadable()
    {
        var cmd = ProcessInspector.GetCommandLine(Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(cmd));
    }

    private static async Task WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(100);
    }
}

public class UninstallScriptTests
{
    [Fact]
    public async Task CleanupWaitsForTheProcessThenDeletesFoldersAndItself()
    {
        var dir = Directory.CreateTempSubdirectory("lucycleanup").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "f.txt"), "x");

        // Stand-in for Lucy.exe: a process that stays alive until we kill it.
        using var target = ManagedProcess.Start(new ProcessSpec
        {
            FileName = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            Arguments = new[] { "-n", "60", "127.0.0.1" },
        });
        var script = Path.Combine(Path.GetTempPath(), $"lucy-cleanup-test-{target.Id}.cmd");
        File.WriteAllLines(script, Integration.CleanupScript(target.Id, new[] { dir }));

        // Run it with the test host's PATH (Git Bash puts Unix find first): must still work.
        using var cleaner = ManagedProcess.Start(new ProcessSpec
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            RawArguments = $"/d /c \"\"{script}\"\"",
        });

        await Task.Delay(3500);
        Assert.True(Directory.Exists(dir), "folder must survive while the process is alive");
        Assert.True(cleaner.IsTreeAlive, "script must still be waiting");

        target.Kill();
        await cleaner.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(Directory.Exists(dir));
        Assert.False(File.Exists(script));
    }
}
