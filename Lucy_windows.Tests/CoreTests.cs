using System.Net;
using System.Net.Sockets;
using Lucy.Core;
using Lucy.Launcher;

namespace Lucy.Tests;

public class DotEnvTests
{
    [Fact]
    public void DevModeRoundTripMatchesLucyPy()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("lucyenv").FullName, ".env");
        Assert.False(DotEnv.GetDevMode(path));
        File.WriteAllText(path, "PORT_ROSBRIDGE=9090\n# comment\n");
        DotEnv.SetDevMode(path, true);
        Assert.True(DotEnv.GetDevMode(path));
        Assert.Equal("9090", DotEnv.Read(path)["PORT_ROSBRIDGE"]);
        DotEnv.SetDevMode(path, false);
        Assert.False(DotEnv.GetDevMode(path));
        Assert.Single(File.ReadAllLines(path), l => l.StartsWith("DEV="));
    }

    [Fact]
    public void QuotesAreStripped()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("lucyenv").FullName, ".env");
        File.WriteAllText(path, "A=\"x y\"\nB='z'\nexport C=1\n");
        var env = DotEnv.Read(path);
        Assert.Equal("x y", env["A"]);
        Assert.Equal("z", env["B"]);
        Assert.Equal("1", env["C"]);
    }
}

public class CommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void QuotesLikeCommandLineToArgv(string input, string expected) => Assert.Equal(expected, CommandLine.Quote(input));

    [Fact]
    public void TokenizesShellStyle()
    {
        Assert.Equal(new[] { "pgrep", "-f", "bin/[r]os2 launch" }, CommandLine.Tokenize("pgrep -f 'bin/[r]os2 launch'"));
        Assert.Equal(new[] { "echo", "a \"b\"" }, CommandLine.Tokenize("echo \"a \\\"b\\\"\""));
    }

    [Fact]
    public void ExpandsPosixDefaults()
    {
        var env = new Dictionary<string, string> { ["A"] = "1" };
        Assert.Equal("1 9090 ${B}", ShellEnv.Expand("${A} ${PORT:-9090} ${B}", env));
    }
}

public class PortResolverTests
{
    [Fact]
    public void SkipsBusyPortsAndReadsPanelEnv()
    {
        var dir = Directory.CreateTempSubdirectory("lucyports").FullName;
        var ws = new Workspace(dir);
        Directory.CreateDirectory(ws.ControlPanelDir);
        File.WriteAllText(Path.Combine(ws.ControlPanelDir, ".env"), "VITE_PORT=5000\nVITE_HTTPS=true\n");
        var r = PortResolver.Resolve(ws, new Dictionary<string, string>(), p => p is 5000 or 5001);
        Assert.Equal(5002, r.HostPort);
        Assert.Equal(5000, r.ContainerPort);
        Assert.Equal("https", r.Scheme);
    }

    [Fact]
    public void DefaultsTo4004Http()
    {
        var ws = new Workspace(Directory.CreateTempSubdirectory("lucyports").FullName);
        var r = PortResolver.Resolve(ws, new Dictionary<string, string>(), _ => false);
        Assert.Equal(4004, r.HostPort);
        Assert.Equal("http", r.Scheme);
        Assert.Equal("4004", r.Env["LUCY_LCP_PUBLISHED_HOST_PORT"]);
    }
}

public class ReadinessTests
{
    private static ProbeContext Ctx(params string[] cmdlines) => new(
        new Dictionary<string, string>(), @"C:\Lucy",
        () => cmdlines.Select((c, i) => new ProcessSnapshot(i + 1, Path.GetFileNameWithoutExtension(c.Split(' ')[0].Trim('"')), c, "")).ToList());

    [Fact]
    public void PgrepMatchesWindowsCommandLines()
    {
        var ctx = Ctx(@"""C:\Lucy\.pixi\envs\default\Scripts\ros2.exe"" launch lucy_bringup lucy.launch.py robot_package:=inmoov_urdf",
                      @"C:\Lucy\.pixi\envs\default\python.exe C:\Lucy\.pixi\envs\default\Library\lib\rosbridge_server\rosbridge_websocket --ros-args");
        Assert.True(ReadinessProbe.Evaluate("pgrep -f 'bin/[r]os2 launch lucy_bringup' >/dev/null 2>&1", ctx));
        Assert.True(ReadinessProbe.Evaluate("pgrep -f '[r]osbridge_websocket' >/dev/null 2>&1", ctx));
        Assert.False(ReadinessProbe.Evaluate("pgrep -f '[c]onfig_pipeline_node' >/dev/null 2>&1", ctx));
    }

    [Fact]
    public void PgrepExactMatchesImageName()
    {
        var ctx = Ctx(@"C:\Lucy\.pixi\envs\default\Library\bin\rviz2.exe -d foo.rviz");
        Assert.True(ReadinessProbe.Evaluate("pgrep -x rviz2 >/dev/null 2>&1", ctx));
        Assert.False(ReadinessProbe.Evaluate("pgrep -x rqt", ctx));
    }

    [Fact]
    public void PosixOnlyChecksPass()
    {
        var ctx = Ctx();
        Assert.True(ReadinessProbe.Evaluate("bash scripts/controllers_active.sh 1", ctx));
        Assert.True(ReadinessProbe.Evaluate("test -f /tmp/lucy_cli.ready", ctx));
    }

    [Fact]
    public void AndChainsRequireEveryPart()
    {
        var ctx = Ctx("foo.exe lucy_embedded_firmware_sim");
        Assert.True(ReadinessProbe.Evaluate("test -f /tmp/.lucy_real_hardware_ready && pgrep -f '[l]ucy_embedded_firmware_sim' >/dev/null 2>&1", ctx));
        Assert.False(ReadinessProbe.Evaluate("test -f /tmp/x && pgrep -f '[n]ope'", ctx));
    }

    [Fact]
    public void PortCheckConnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ctx = new ProbeContext(new Dictionary<string, string> { ["P"] = port.ToString() }, @"C:\Lucy", () => new());
            Assert.True(ReadinessProbe.Evaluate("bash scripts/port_open.sh ${P:-1}", ctx));
        }
        finally { listener.Stop(); }
        Assert.False(ReadinessProbe.IsPortOpen(1, 200));
    }
}

public class StackGuardTests
{
    [Fact]
    public void MatchesOnlyStackProcessesOfThisWorkspace()
    {
        var root = @"C:\Lucy";
        ProcessSnapshot P(string cmd, string exe = "") => new(42, "python", cmd, exe);
        Assert.True(StackGuard.IsLucyOrphan(P(@"C:\Lucy\.pixi\envs\default\python.exe C:\Lucy\.pixi\envs\default\Library\lib\rosbridge_server\rosbridge_websocket"), root, 1));
        Assert.False(StackGuard.IsLucyOrphan(P(@"C:\Other\python.exe rosbridge_websocket"), root, 1));
        Assert.True(StackGuard.IsLucyOrphan(P(@"node C:\Lucy\src\lucy_control_panel\node_modules\vite\bin\vite.js"), root, 1));
        Assert.False(StackGuard.IsLucyOrphan(P(@"node C:\Lucy\docs\vite.js"), root, 1));
        Assert.False(StackGuard.IsLucyOrphan(P(@"C:\Lucy\.pixi\envs\default\python.exe -m launcher"), root, 1));
        Assert.False(StackGuard.IsLucyOrphan(P(@"notepad C:\Lucy\README.md"), root, 1));
    }
}

public class UninstallTests
{
    [Fact]
    public void OnlyCheckoutsAreDeletable()
    {
        var dir = Directory.CreateTempSubdirectory("lucyuninst").FullName;
        Assert.False(Integration.IsSafeToDelete(dir));            // plain folder
        File.WriteAllText(Path.Combine(dir, "pixi.toml"), "");
        File.WriteAllText(Path.Combine(dir, "install.py"), "");
        Assert.True(Integration.IsSafeToDelete(dir));             // lucy_ws checkout
        Assert.True(Integration.IsSafeToDelete(dir + "\\"));
        Assert.False(Integration.IsSafeToDelete(@"C:\"));
        Assert.False(Integration.IsSafeToDelete(Path.Combine(dir, "missing")));
    }
}
