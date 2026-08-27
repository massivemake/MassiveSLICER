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
        if (!File.Exists(PrefsPath))
        {
            var fresh = new AppPreferences();
            // Travel Moves is on by default; do not also turn on Robot Mode MAT.
            fresh.RobotModeEnabled = false;
            return fresh;
        }
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
            var fresh = new AppPreferences();
            fresh.RobotModeEnabled = false;
            return fresh;
        }
    }

    private static void MigrateLegacyPrefs(AppPreferences prefs)
    {
        if (prefs.WipeModeDisplay is "Natural" or "Normal")
            prefs.WipeModeDisplay = "Same-Direction";

        // Old factory was Wipe Off + 10 mm / 120 mm/s (or the half-migrated 12 / 600).
        bool factoryWipeOff = prefs.WipeModeDisplay is "Off" or null or "";
        bool factoryWipeNums = (prefs.WipeLengthMm is 10.0 or 12.0)
            && (prefs.WipeSpeed is 120.0 or 600.0);
        if (factoryWipeOff && factoryWipeNums)
        {
            prefs.WipeModeDisplay = "Same-Direction";
            prefs.WipeLengthMm = 35.0;
            prefs.WipeSpeed = 600.0;
            prefs.WipeRampMm = -1.0;
        }

        if (!prefs.DigitalStartStopEnabled)
        {
            prefs.DigitalStartStopEnabled = true;
            // Enabling travel start/stop must not flip Robot Mode on for pre-split prefs.
            prefs.RobotModeEnabled ??= false;
        }
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
