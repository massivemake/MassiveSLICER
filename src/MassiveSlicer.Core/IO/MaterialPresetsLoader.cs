using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Persists the user's material preset library as JSON in AppData.
/// Path: <c>%AppData%\MassiveSlicer\materials.json</c>
/// </summary>
public static class MaterialPresetsLoader
{
    private static readonly string PresetsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer");

    private static readonly string PresetsPath = Path.Combine(PresetsDir, "materials.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    public static List<MaterialPreset> Load()
    {
        if (!File.Exists(PresetsPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<MaterialPreset>>(
                File.ReadAllText(PresetsPath), Options) ?? [];
        }
        catch { return []; }
    }

    public static void Save(IEnumerable<MaterialPreset> presets)
    {
        try
        {
            Directory.CreateDirectory(PresetsDir);
            File.WriteAllText(PresetsPath, JsonSerializer.Serialize(presets.ToList(), Options));
        }
        catch { }
    }
}
