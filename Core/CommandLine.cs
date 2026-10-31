using System.Text;

namespace Lucy.Core;

public static class CommandLine
{
    /// <summary>Quote one argument following the MSVCRT / CommandLineToArgvW rules.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    /// <summary>
    /// Split a POSIX-shell-like command (as written in launcher_config.json) into words.
    /// Handles single/double quotes and backslash escapes; no expansion.
    /// </summary>
    public static List<string> Tokenize(string command)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        var inWord = false;
        char quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; continue; }
                if (c == '\\' && quote == '"' && i + 1 < command.Length && (command[i + 1] == '"' || command[i + 1] == '\\'))
                {
                    cur.Append(command[++i]);
                    continue;
                }
                cur.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inWord) { words.Add(cur.ToString()); cur.Clear(); inWord = false; }
                continue;
            }
            inWord = true;
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '\\' && i + 1 < command.Length) { cur.Append(command[++i]); continue; }
            cur.Append(c);
        }
        if (inWord) words.Add(cur.ToString());
        return words;
    }
}
