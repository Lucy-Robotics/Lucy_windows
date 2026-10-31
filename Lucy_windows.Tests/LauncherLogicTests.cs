using Lucy.Core;
using Lucy.Launcher;

namespace Lucy.Tests;

public class ConfigTests
{
    [Fact]
    public void ParsesEveryPackageAndField()
    {
        var c = TestData.Configs();
        Assert.Equal(11, c.Count);
        var core = c.Single(p => p.Id == "core");
        Assert.True(core.IsCore);
        Assert.Equal(4, core.ReadinessStages.Count);
        Assert.Equal(150, core.ReadinessTimeout);
        var robot = c.Single(p => p.Id == "robot_inmoov");
        Assert.Equal("inmoov_urdf", robot.RequiresPkg);
        Assert.True(robot.DefaultOn);
        Assert.True(c.Single(p => p.Id == "headless").Subitem);
        Assert.Equal("Ctrl-B W", c.Single(p => p.Id == "lucy_cli").NavHint);
        Assert.Equal(30, c.Single(p => p.Id == "rviz").ReadinessTimeout);
    }

    [Fact]
    public void ParsesTheRealWorkspaceConfigWhenPresent()
    {
        const string real = @"C:\Dev\lucy_ws\config\launcher_config.json";
        if (!File.Exists(real)) return;
        var c = LauncherConfigLoader.Parse(File.ReadAllText(real));
        Assert.Contains(c, p => p.Id == "core");
        Assert.Contains(c, p => p.Id == "control_panel");
    }

    [Fact]
    public void LegacyComplexCommandIsParsed()
    {
        var c = LauncherConfigLoader.Parse("""{"packages":[{"id":"x","name":"X","type":"tool","command":{"start":"a","stop":"b","is_running":"c"}}]}""");
        Assert.True(c[0].IsComplex);
        Assert.Equal("a", c[0].ComplexStart);
        Assert.Equal("b", c[0].ComplexStop);
    }

    [Fact]
    public void LocalConfigOverridesTrackedFile()
    {
        var dir = Directory.CreateTempSubdirectory("lucytest").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Path.Combine(dir, "config", "launcher_config.json"), """{"packages":[{"id":"a","name":"A","type":"tool"}]}""");
        var ws = new Workspace(dir);
        Assert.Equal("a", LauncherConfigLoader.Load(ws).Single().Id);
        File.WriteAllText(Path.Combine(dir, "config", "launcher_config.json.local"), """{"packages":[{"id":"b","name":"B","type":"tool"}]}""");
        Assert.Equal("b", LauncherConfigLoader.Load(ws).Single().Id);
    }
}

public class SelectionTests
{
    [Fact]
    public void EnablingPullsInDependencies()
    {
        var s = TestData.State();
        s.Toggle("rviz");
        Assert.True(s.Get("rviz")!.Selected);
        Assert.True(s.Get("core")!.Selected);
    }

    [Fact]
    public void EnablingClearsConflicts()
    {
        var s = TestData.State();
        Assert.True(s.Get("robot_inmoov")!.Selected); // default_on
        s.Toggle("robot_so_arm101");
        Assert.True(s.Get("robot_so_arm101")!.Selected);
        Assert.False(s.Get("robot_inmoov")!.Selected);
    }

    [Fact]
    public void DisablingCascadesToDependents()
    {
        var s = TestData.State();
        s.Toggle("rviz");
        s.Toggle("lucy_cli");
        s.Toggle("core");
        Assert.False(s.Get("core")!.Selected);
        Assert.False(s.Get("rviz")!.Selected);
        Assert.False(s.Get("lucy_cli")!.Selected);
    }

    [Fact]
    public void LastRobotCannotBeUnticked()
    {
        var s = TestData.State();
        s.Toggle("core");
        Assert.Equal("One robot package required", s.Toggle("robot_inmoov"));
        Assert.True(s.Get("robot_inmoov")!.Selected);
    }

    [Fact]
    public void GazeboAndItsDependentsAreRefusedOnWindows()
    {
        var s = TestData.State();
        Assert.NotNull(s.Toggle("gazebo"));
        Assert.False(s.Get("gazebo")!.Selected);
        Assert.NotNull(s.Toggle("headless"));
        Assert.False(s.Get("headless")!.Selected);
    }

    [Fact]
    public void StillStoppingBlocksReEnable()
    {
        var s = TestData.State();
        s.Get("control_panel")!.StopRequestedAt = DateTime.Now;
        Assert.Equal("Still stopping…", s.Toggle("control_panel"));
    }

    [Fact]
    public void RestoreSelectionKeepsOneRobotAndDropsUnsupported()
    {
        var s = TestData.State();
        s.RestoreSelection(new HashSet<string> { "core", "robot_so_arm101", "gazebo", "control_panel" });
        Assert.True(s.Get("core")!.Selected);
        Assert.True(s.Get("robot_so_arm101")!.Selected);
        Assert.False(s.Get("robot_inmoov")!.Selected);
        Assert.False(s.Get("gazebo")!.Selected);
        Assert.True(s.Get("control_panel")!.Selected);
    }

    [Fact]
    public void DefaultRobotIsInMoov()
    {
        var s = TestData.State();
        s.Get("robot_inmoov")!.Selected = false;
        s.Get("core")!.Selected = true;
        s.DefaultRobotSelection();
        Assert.True(s.Get("robot_inmoov")!.Selected);
    }

    [Fact]
    public void SectionsFollowTuiOrder()
    {
        var sections = TestData.State().Sections();
        Assert.Equal(new[] { "Primary Launch Targets", "Robot", "Interfaces", "Tools" }, sections.Select(x => x.Section));
        Assert.Equal("core", sections[0].Items[0].Id);
        Assert.Equal(2, sections[0].Items.Single(i => i.Id == "headless").IndentLevel);
        Assert.Equal(1, sections[0].Items.Single(i => i.Id == "rviz").IndentLevel);
    }
}

public class CommandMapperTests
{
    private const string Pixi = @"C:\pixi\pixi.exe";

    [Fact]
    public void CoreGoesThroughPixiLucyLaunchWithRobotAsEnv()
    {
        var c = TestData.Configs();
        var core = c.Single(p => p.Id == "core");
        var mods = c.Where(p => p.Id is "robot_so_arm101" or "rviz" or "real");
        var plan = WindowsCommandMapper.ForCore(core, mods, Pixi);
        Assert.Equal(Pixi, plan.FileName);
        Assert.Equal(new[] { "run", "--", "python", "scripts/pixi_lucy_launch.py", "rviz:=true", "real:=true" }, plan.Arguments);
        Assert.Equal("so_arm101_urdf", plan.ExtraEnv["LUCY_ROBOT_PACKAGE"]);
        Assert.Equal(ConsoleMode.Captured, plan.Mode);
    }

    [Fact]
    public void PixiCommandsRunAsIs()
    {
        var plan = WindowsCommandMapper.ForPackage(TestData.Configs().Single(p => p.Id == "control_panel"), Pixi);
        Assert.Equal(new[] { "run", "panel-dev" }, plan.Arguments);
        Assert.Equal(ConsoleMode.Captured, plan.Mode);
    }

    [Fact]
    public void InteractiveInterfacesGetTheirOwnWindow()
    {
        var plan = WindowsCommandMapper.ForPackage(TestData.Configs().Single(p => p.Id == "lucy_cli"), Pixi);
        Assert.Equal(new[] { "run", "--", "ros2", "run", "lucy_cli", "tui" }, plan.Arguments);
        Assert.Equal(ConsoleMode.NewWindow, plan.Mode);
    }

    [Fact]
    public void ConsoleBecomesPixiShell()
    {
        var plan = WindowsCommandMapper.ForPackage(TestData.Configs().Single(p => p.Id == "console"), Pixi);
        Assert.Equal(new[] { "shell" }, plan.Arguments);
        Assert.Equal(ConsoleMode.NewWindow, plan.Mode);
    }

    [Fact]
    public void ToolsPauseOnExitInTheirWindow()
    {
        var plan = WindowsCommandMapper.ForPackage(TestData.Configs().Single(p => p.Id == "rqt"), Pixi);
        Assert.EndsWith("cmd.exe", plan.FileName);
        Assert.Contains("run -- rqt", plan.RawArguments);
        Assert.Contains("press any key", plan.RawArguments);
    }

    [Fact]
    public void CommandWinOverrideWins()
    {
        var c = LauncherConfigLoader.Parse("""{"packages":[{"id":"x","name":"X","type":"interface","command":"bash foo.sh","command_win":"pixi run foo"}]}""");
        Assert.Equal(new[] { "run", "foo" }, WindowsCommandMapper.ForPackage(c[0], Pixi).Arguments);
    }

    [Fact]
    public void GazeboIsUnsupported()
    {
        var g = TestData.Configs().Single(p => p.Id == "gazebo");
        Assert.NotNull(WindowsCommandMapper.UnsupportedReason(g));
        Assert.Null(WindowsCommandMapper.UnsupportedReason(TestData.Configs().Single(p => p.Id == "rviz")));
    }
}
