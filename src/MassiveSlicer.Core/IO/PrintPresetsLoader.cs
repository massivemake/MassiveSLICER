using System.Text.Json;
using System.Text.Json.Serialization;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// One saved print preset — a named snapshot of a subset of Additive slicing settings.
/// Field names mirror <c>AdditiveSettingsViewModel</c>. This is the real, persisted schema
/// (unlike the earlier in-memory-only comp); it only covers the fields that have a direct,
/// unambiguous real-settings counterpart today (bead width, layer height, print speed, seam
/// mode, X-bracing, pattern, method) — not yet the full ~100-property settings surface.
/// </summary>
public sealed class PrintPresetRecord
{
    public required string Name { get; set; }
    public double BeadWidth { get; set; }
    public double LayerHeight { get; set; }
    public double PrintSpeed { get; set; }
    public string Method { get; set; } = "Planar";
    public string PatternType { get; set; } = "Smooth";
    public string SeamMode { get; set; } = "Normal";
    public bool XBracingEnabled { get; set; }
    public string Material { get; set; } = "ABS";
    public string Folder { get; set; } = "Uncategorized";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastPrintedUtc { get; set; }
    public bool IsFavorite { get; set; }
}

/// <summary>
/// Persists the user's saved/imported print presets as JSON in the user's AppData folder.
/// Path: <c>%AppData%\MassiveSlicer\presets.json</c>. Local-only for now (matches
/// <see cref="PreferencesLoader"/>'s convention) — a shared/synced library is a later,
/// separate step (see project notes on one-file-per-preset + git for that).
/// Seeded/sample presets are never written here — only presets the user actually saved or
/// imported.
/// </summary>
public static class PrintPresetsLoader
{
    private static readonly string PresetsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer");

    private static readonly string PresetsPath = Path.Combine(PresetsDir, "presets.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads saved presets from disk. Returns an empty list if the file doesn't exist or can't be parsed.</summary>
    public static List<PrintPresetRecord> Load()
    {
        if (!File.Exists(PresetsPath)) return [];
        try
        {
            var json = File.ReadAllText(PresetsPath);
            return JsonSerializer.Deserialize<List<PrintPresetRecord>>(json, Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Serializes the given presets to disk, creating the directory if needed.</summary>
    public static void Save(IEnumerable<PrintPresetRecord> presets)
    {
        try
        {
            Directory.CreateDirectory(PresetsDir);
            File.WriteAllText(PresetsPath, JsonSerializer.Serialize(presets.ToList(), Options));
        }
        catch { /* non-fatal -- same as PreferencesLoader; don't crash the app over a disk write */ }
    }
}
