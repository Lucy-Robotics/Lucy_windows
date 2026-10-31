namespace Lucy.Core;

public static class AppPaths
{
    public const string AppName = "Lucy";
    public const string DefaultWorkspace = @"C:\Lucy";
    public const string RepoUrl = "https://github.com/Lucy-Robotics/lucy_ws.git";
    public const string RepoUrlSsh = "git@github.com:Lucy-Robotics/lucy_ws.git";
    public const string DefaultBranch = "master";
    public const string WebsiteUrl = "https://lucy-robotics.com";

    public static string DataDir { get; } = Path.Combine(ShellEnv.LocalAppData, AppName);
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string InstalledExeDir => Path.Combine(ShellEnv.LocalAppData, "Programs", AppName);
    public static string InstalledExe => Path.Combine(InstalledExeDir, "Lucy.exe");
    public static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
    public static string StartMenuShortcut => Path.Combine(StartMenuDir, "Lucy.lnk");

    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Lucy.exe");
}
