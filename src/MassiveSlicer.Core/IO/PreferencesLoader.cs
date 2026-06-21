using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Persists <see cref="AppPreferences"/> as JSON in the user's AppData folder.
/// Path: <c>%AppData%\MassiveSlicer\prefs.json</c>
/// </summary>
public static class PreferencesLoader
{
    private static readonly string PrefsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer");

    private static readonly string PrefsPath = Path.Combine(PrefsDir, "prefs.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented                = true,
        PropertyNameCaseInsensitive  = true,
        ReadCommentHandling          = JsonCommentHandling.Skip,
        AllowTrailingCommas          = true,
        Converters                   = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Loads preferences from disk. Returns defaults if the file does not exist
    /// or cannot be parsed.
    /// </summary>
    public static AppPreferences Load()
    {
        if (!File.Exists(PrefsPath)) return new AppPreferences();
        try
        {
            string json = File.ReadAllText(PrefsPath);
            var prefs = JsonSerializer.Deserialize<AppPreferences>(json, Options)
                        ?? new AppPreferences();
            MigrateLegacyPrefs(prefs);
            return prefs;
        }
        catch
        {
            return new AppPreferences();
        }
    }

    private static void MigrateLegacyPrefs(AppPreferences prefs)
    {
        if (prefs.WipeModeDisplay is "Natural" or "Normal")
            prefs.WipeModeDisplay = "Same-Direction";
    }

    /// <summary>Serialises <paramref name="prefs"/> to disk, creating the directory if needed.</summary>
    public static void Save(AppPreferences prefs)
    {
        try
        {
            Directory.CreateDirectory(PrefsDir);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs, Options));
        }
        catch { /* non-fatal -- preferences are nice-to-have, not required */ }
    }
}
