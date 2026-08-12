using System.Text.Json;
using TorsherPlayer.Models;

namespace TorsherPlayer.Infrastructure;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static PlayerSettings Load(FileLogger log)
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return new PlayerSettings();
            return Normalize(JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new PlayerSettings());
        }
        catch (Exception ex) { log.Exception(ex, "Settings load failed"); return new PlayerSettings(); }
    }
    public static PlayerSettings Normalize(PlayerSettings settings)
    {
        settings.UiScale = Math.Clamp(double.IsFinite(settings.UiScale) ? settings.UiScale : 1.80, .9, 2.5);
        settings.CompletedAt = Math.Clamp(double.IsFinite(settings.CompletedAt) ? settings.CompletedAt : .90, .01, 1.0);
        settings.ProgressEverySeconds = Math.Clamp(settings.ProgressEverySeconds, 2, 60);
        settings.Theme = ThemeCatalog.Normalize(settings.Theme);
        settings.HardwareDecoding = settings.HardwareDecoding switch
        {
            "no" or "auto" or "auto-copy" => settings.HardwareDecoding,
            _ => "auto-copy"
        };
        return settings;
    }

    public static void Save(PlayerSettings settings, FileLogger log)
    {
        try
        {
            Normalize(settings);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex) { log.Exception(ex, "Settings save failed"); }
    }
}
