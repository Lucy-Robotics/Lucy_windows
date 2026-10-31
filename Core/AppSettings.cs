using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucy.Core;

/// <summary>App-level settings (%LOCALAPPDATA%\Lucy\settings.json). Workspace state stays in the workspace.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("workspace")] public string? Workspace { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("installed_at")] public DateTime? InstalledAt { get; set; }
    [JsonPropertyName("last_tab")] public string? LastTab { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Warn($"settings unreadable, starting fresh: {ex.Message}"); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Error("could not save settings", ex); }
    }
}
