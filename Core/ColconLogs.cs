using System.Text.RegularExpressions;

namespace Lucy.Core;

/// <summary>
/// colcon only echoes a package's stderr; MSVC and CMake write their errors to stdout, so a failed
/// package often shows nothing but "Failed &lt;&lt;&lt; pkg". The full output is in
/// log/latest_build/&lt;pkg&gt;/stdout_stderr.log: pull the relevant part into the install log.
/// </summary>
public static class ColconLogs
{
    private static readonly Regex FailedPkg = new(@"^Failed\s+<<<\s+(?<pkg>[\w.-]+)", RegexOptions.Multiline);
    private static readonly Regex ErrorLine = new(@"(\berror\b|\bError\b|CMake Error|fatal error|undefined reference|unresolved external|LNK\d{4}|C\d{4}:)", RegexOptions.Compiled);

    public static List<string> FailedPackages(string output) =>
        FailedPkg.Matches(output).Select(m => m.Groups["pkg"].Value).Distinct().ToList();

    /// <summary>Lines to append after a failed build: error lines first, then the tail of each log.</summary>
    public static List<string> FailureDetails(Workspace ws, string output, int tail = 40, int maxErrors = 25)
    {
        var result = new List<string>();
        foreach (var pkg in FailedPackages(output))
        {
            var log = Path.Combine(ws.Root, "log", "latest_build", pkg, "stdout_stderr.log");
            if (!File.Exists(log)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(log); }
            catch (IOException) { continue; }

            result.Add("");
            result.Add($"===== {pkg}: build log ({log}) =====");
            var errors = lines.Where(l => ErrorLine.IsMatch(l)).Distinct().Take(maxErrors).ToList();
            if (errors.Count > 0)
            {
                result.Add("-- errors:");
                result.AddRange(errors.Select(e => "   " + e.TrimEnd()));
            }
            result.Add($"-- last {Math.Min(tail, lines.Length)} lines:");
            result.AddRange(lines.TakeLast(tail).Select(l => "   " + l.TrimEnd()));
        }
        return result;
    }
}
