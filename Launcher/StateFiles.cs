using System.Text.Json;
using Lucy.Core;

namespace Lucy.Launcher;

/// <summary>The TUI's state files, read and written in the same format (launcher/config.py).</summary>
public static class StateFiles
{
    /// <summary>.lucy_launcher_state.json {"selected": [...]}; null if never saved.</summary>
    public static HashSet<string>? LoadSelection(Workspace ws)
    {
        try
        {
            if (!File.Exists(ws.SelectionFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ws.SelectionFile));
            if (!doc.RootElement.TryGetProperty("selected", out var sel) || sel.ValueKind != JsonValueKind.Array)
                return new HashSet<string>();
            return sel.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet();
        }
        catch { return null; }
    }

    public static void SaveSelection(Workspace ws, IEnumerable<string> ids)
    {
        try { File.WriteAllText(ws.SelectionFile, JsonSerializer.Serialize(new { selected = ids.OrderBy(i => i, StringComparer.Ordinal).ToArray() })); }
        catch (Exception ex) { Log.Warn($"could not save selection: {ex.Message}"); }
    }

    /// <summary>.lucy_launcher_modifiers.json {"modifiers": [...]} — modifiers core is running with.</summary>
    public static void SaveModifiers(Workspace ws, IEnumerable<string> ids)
    {
        try { File.WriteAllText(ws.ModifiersFile, JsonSerializer.Serialize(new { modifiers = ids.ToArray() })); }
        catch (Exception ex) { Log.Warn($"could not save modifiers: {ex.Message}"); }
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Warn($"could not delete {path}: {ex.Message}"); }
    }
}
