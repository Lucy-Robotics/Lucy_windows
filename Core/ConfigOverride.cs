using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lucy.Core;

public enum ConfigKind { Repos, Launcher }

/// <summary>
/// A gitignored <c>config/&lt;name&gt;.local</c> override of a tracked lucy_ws config file. install.py
/// reads repos.json.local instead of repos.json when it exists, and the launcher does the same for
/// launcher_config.json(.local).
/// </summary>
public sealed class ConfigOverride
{
    private static readonly UTF8Encoding Utf8NoBom = new(false); // Python's json.load chokes on a BOM

    public ConfigOverride(Workspace ws, ConfigKind kind)
    {
        Kind = kind;
        var name = kind == ConfigKind.Repos ? "repos.json" : "launcher_config.json";
        BasePath = Path.Combine(ws.Root, "config", name);
        LocalPath = BasePath + ".local";
        ExamplePath = BasePath + ".local.example";
    }

    public ConfigKind Kind { get; }
    public string BasePath { get; }
    public string LocalPath { get; }
    public string ExamplePath { get; }
    public string FileName => Path.GetFileName(LocalPath);

    public bool HasLocal => File.Exists(LocalPath);
    public bool HasExample => File.Exists(ExamplePath);

    public string ReadBase() => File.Exists(BasePath) ? File.ReadAllText(BasePath) : "";
    public string ReadExample() => HasExample ? File.ReadAllText(ExamplePath) : "";

    /// <summary>What the editor starts with: the current override, else a copy of the tracked file.</summary>
    public string ReadCurrent() => HasLocal ? File.ReadAllText(LocalPath) : ReadBase();

    /// <summary>Null when valid; otherwise a one-line message (with the line number for syntax errors).</summary>
    public string? Validate(string text) => Validate(Kind, text);

    public static string? Validate(ConfigKind kind, string text)
    {
        JsonNode? root;
        try
        {
            // Strict, like Python's json module: no comments, no trailing commas.
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions());
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is long l ? $"line {l + 1}: " : "";
            return "Invalid JSON, " + line + FirstSentence(ex.Message);
        }
        if (root is not JsonObject obj) return "The file must contain a JSON object { ... }.";

        if (kind == ConfigKind.Repos)
        {
            if (obj["repos"] is not JsonArray repos) return "Missing \"repos\": [ ... ] list.";
            if (repos.Count == 0) return "\"repos\" is empty: no repository would be fetched.";
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < repos.Count; i++)
            {
                if (repos[i] is not JsonObject r) return $"repos[{i}] must be an object.";
                var name = Str(r, "name");
                if (name == null) return $"repos[{i}] has no \"name\".";
                if (!names.Add(name)) return $"Repository \"{name}\" is listed twice.";
                if (Str(r, "url_https") == null && Str(r, "url") == null && Str(r, "url_ssh") == null)
                    return $"Repository \"{name}\" needs \"url_https\" (or \"url_ssh\").";
                if (r["branch"] is JsonNode b && b.GetValueKind() != JsonValueKind.String)
                    return $"Repository \"{name}\": \"branch\" must be a string.";
            }
        }
        else
        {
            if (obj["packages"] is not JsonArray pkgs) return "Missing \"packages\": [ ... ] list.";
            var ids = new HashSet<string>();
            var hasCore = false;
            for (var i = 0; i < pkgs.Count; i++)
            {
                if (pkgs[i] is not JsonObject p) return $"packages[{i}] must be an object.";
                var id = Str(p, "id");
                if (id == null) return $"packages[{i}] has no \"id\".";
                if (!ids.Add(id)) return $"Package \"{id}\" is listed twice.";
                if (Str(p, "name") == null) return $"Package \"{id}\" has no \"name\".";
                var type = Str(p, "type");
                if (type is not ("core" or "modifier" or "interface" or "tool"))
                    return $"Package \"{id}\": \"type\" must be core, modifier, interface or tool.";
                hasCore |= type == "core";
            }
            if (!hasCore) return "No package of type \"core\": the robot stack could not be launched.";
            foreach (var p in pkgs.OfType<JsonObject>())
                foreach (var key in new[] { "dependencies", "conflicts" })
                    if (p[key] is JsonArray deps)
                        foreach (var d in deps)
                            if (d?.GetValueKind() == JsonValueKind.String && !ids.Contains(d.GetValue<string>()) && key == "dependencies")
                                return $"Package \"{Str(p, "id")}\" depends on unknown package \"{d.GetValue<string>()}\".";
        }
        return null;
    }

    /// <summary>Re-indent valid JSON (2 spaces, like the tracked files). Returns the input when invalid.</summary>
    public static string Format(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return node!.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n";
        }
        catch (JsonException) { return text; }
    }

    public enum SaveResult { Written, Removed, Unchanged }

    /// <summary>
    /// Persist the editor content. Content identical to the tracked file means "no override": an
    /// existing .local is then removed so install.py falls back to the tracked file.
    /// </summary>
    public SaveResult Save(string text)
    {
        if (Validate(text) is string error) throw new InvalidOperationException($"{FileName}: {error}");
        if (SameJson(text, ReadBase()))
        {
            if (!HasLocal) return SaveResult.Unchanged;
            File.Delete(LocalPath);
            return SaveResult.Removed;
        }
        if (HasLocal && File.ReadAllText(LocalPath) == text) return SaveResult.Unchanged;
        File.WriteAllText(LocalPath, text, Utf8NoBom);
        return SaveResult.Written;
    }

    internal static bool SameJson(string a, string b)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (JsonException) { return false; }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonNode n && n.GetValueKind() == JsonValueKind.String && n.GetValue<string>().Trim().Length > 0
            ? n.GetValue<string>().Trim() : null;

    private static string FirstSentence(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0) cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).TrimEnd('.');
    }
}
