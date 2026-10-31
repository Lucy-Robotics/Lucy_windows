using System.Text.Json;
using Lucy.Core;

namespace Lucy.Launcher;

public sealed record ReadinessStage(string Label, string Check);

/// <summary>One entry of config/launcher_config.json (schema of launcher/package.py).</summary>
public sealed class PackageConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; } // core | modifier | interface | tool
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    /// <summary>Plain command string ("" when the legacy {start,stop,is_running} form is used).</summary>
    public string Command { get; init; } = "";
    public string? ComplexStart { get; init; }
    public string? ComplexStop { get; init; }
    public IReadOnlyDictionary<string, string> LifecycleHooks { get; init; } = new Dictionary<string, string>();
    public bool DefaultOn { get; init; }
    public string? RequiresPkg { get; init; }
    public string? RequiresEnv { get; init; }
    public bool DevOnly { get; init; }
    public bool Subitem { get; init; }
    public string? ReadinessCheck { get; init; }
    public IReadOnlyList<ReadinessStage> ReadinessStages { get; init; } = Array.Empty<ReadinessStage>();
    public double ReadinessTimeout { get; init; } = LauncherConstants.LoadingTimeout;
    public string? ExitCheck { get; init; }
    public string? Url { get; init; }
    public string NavHint { get; init; } = "";

    // Optional Windows overrides. lucy_ws does not ship these today; they are honoured if added.
    public string? CommandWin { get; init; }
    public string? ReadinessCheckWin { get; init; }
    public bool? WindowsSupported { get; init; }

    public bool IsCore => Type == "core";
    public bool IsModifier => Type == "modifier";
    public bool IsInterface => Type == "interface";
    public bool IsTool => Type == "tool";
    public bool IsComplex => ComplexStart != null;
}

public static class LauncherConstants
{
    public const double LoadingTimeout = 30;   // launcher/constants.py LOADING_TIMEOUT
    public const double StoppingTimeout = 30;  // STOPPING_TIMEOUT
    public const int RosLogRetentionDays = 7;
}

public static class LauncherConfigLoader
{
    /// <summary>config/launcher_config.json.local (gitignored) overrides the tracked file.</summary>
    public static string ResolvePath(Workspace ws) => File.Exists(ws.LauncherConfigLocal) ? ws.LauncherConfigLocal : ws.LauncherConfig;

    public static List<PackageConfig> Load(Workspace ws)
    {
        var path = ResolvePath(ws);
        if (!File.Exists(path)) throw new FileNotFoundException($"Configuration file not found at {path}");
        return Parse(File.ReadAllText(path));
    }

    public static List<PackageConfig> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var list = new List<PackageConfig>();
        if (!doc.RootElement.TryGetProperty("packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var p in pkgs.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var id = Str(p, "id");
            var name = Str(p, "name");
            var type = Str(p, "type");
            if (id == null || name == null || type == null) continue;

            string command = "";
            string? complexStart = null, complexStop = null;
            if (p.TryGetProperty("command", out var cmd))
            {
                if (cmd.ValueKind == JsonValueKind.String) command = cmd.GetString() ?? "";
                else if (cmd.ValueKind == JsonValueKind.Object)
                {
                    complexStart = Str(cmd, "start") ?? "";
                    complexStop = Str(cmd, "stop");
                }
            }

            var stages = new List<ReadinessStage>();
            if (p.TryGetProperty("readiness_stages", out var st) && st.ValueKind == JsonValueKind.Array)
                foreach (var s in st.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    var label = Str(s, "label");
                    var check = Str(s, "check");
                    if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(check)) stages.Add(new ReadinessStage(label, check));
                }

            var hooks = new Dictionary<string, string>();
            if (p.TryGetProperty("lifecycle_hooks", out var lh) && lh.ValueKind == JsonValueKind.Object)
                foreach (var h in lh.EnumerateObject())
                    if (h.Value.ValueKind == JsonValueKind.String) hooks[h.Name] = h.Value.GetString()!;

            list.Add(new PackageConfig
            {
                Id = id,
                Name = name,
                Type = type,
                Description = Str(p, "description") ?? "",
                Dependencies = StrList(p, "dependencies"),
                Conflicts = StrList(p, "conflicts"),
                Command = command,
                ComplexStart = complexStart,
                ComplexStop = complexStop,
                LifecycleHooks = hooks,
                DefaultOn = Bool(p, "default_on"),
                RequiresPkg = Str(p, "requires_pkg"),
                RequiresEnv = Str(p, "requires_env"),
                DevOnly = Bool(p, "dev_only"),
                Subitem = Bool(p, "subitem"),
                ReadinessCheck = Str(p, "readiness_check"),
                ReadinessStages = stages,
                ReadinessTimeout = p.TryGetProperty("readiness_timeout", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetDouble() : LauncherConstants.LoadingTimeout,
                ExitCheck = Str(p, "exit_check"),
                Url = Str(p, "url"),
                NavHint = Str(p, "nav_hint") ?? "",
                CommandWin = Str(p, "command_win"),
                ReadinessCheckWin = Str(p, "readiness_check_win"),
                WindowsSupported = p.TryGetProperty("windows_supported", out var ws) && ws.ValueKind is JsonValueKind.True or JsonValueKind.False ? ws.GetBoolean() : null,
            });
        }
        return list;
    }

    /// <summary>launcher/package.py _pkg_visible.</summary>
    public static bool IsVisible(PackageConfig p, bool devMode, Workspace ws, IDictionary<string, string> env)
    {
        if (p.DevOnly && !devMode) return false;
        if (!string.IsNullOrEmpty(p.RequiresPkg) && !ws.IsRosPackageInstalled(p.RequiresPkg)) return false;
        if (!string.IsNullOrEmpty(p.RequiresEnv))
        {
            env.TryGetValue(p.RequiresEnv, out var v);
            return (v ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes";
        }
        return true;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> StrList(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }
}
