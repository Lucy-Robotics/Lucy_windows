namespace Lucy.Core;

/// <summary>File log in %LOCALAPPDATA%\Lucy\logs (one file per day, 14 kept).</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string Directory { get; } = Path.Combine(AppPaths.DataDir, "logs");
    private static string FilePath => Path.Combine(Directory, $"lucy-{DateTime.Now:yyyyMMdd}.log");

    static Log()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var old in new DirectoryInfo(Directory).GetFiles("lucy-*.log")
                         .OrderByDescending(f => f.Name).Skip(14))
                old.Delete();
        }
        catch { /* logging must never break the app */ }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
