namespace Lucy.Core;

/// <summary>Workspace .env handling with the same semantics as Lucy.py / launcher/config.py.</summary>
public static class DotEnv
{
    /// <summary>Parse KEY=VALUE lines (launcher/config.py load_workspace_env).</summary>
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            if (key.StartsWith("export ")) key = key[7..].Trim();
            if (key.Length == 0) continue;
            result[key] = line[(eq + 1)..].Trim().Trim('"').Trim('\'');
        }
        return result;
    }

    /// <summary>Lucy.py get_dev_mode: the first DEV= line decides.</summary>
    public static bool GetDevMode(string path)
    {
        if (!File.Exists(path)) return false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (!line.StartsWith("DEV=")) continue;
            var parts = line.Split('=');
            return parts.Length > 1 && parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Lucy.py set_dev_mode: rewrite every DEV= line, append one if missing.</summary>
    public static void SetDevMode(string path, bool enabled) => SetValue(path, "DEV", enabled ? "true" : "false");

    public static void SetValue(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var found = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().StartsWith(key + "="))
            {
                lines[i] = $"{key}={value}";
                found = true;
            }
        }
        if (!found) lines.Add($"{key}={value}");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }
}
