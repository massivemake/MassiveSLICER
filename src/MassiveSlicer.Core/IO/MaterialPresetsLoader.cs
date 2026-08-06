using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Persists the material preset library in the user's application-data folder, beside
/// <c>prefs.json</c>: <c>%AppData%/MassiveSlicer/materials.json</c>.
/// <para>
/// It used to live in the repo's <c>assets/</c> folder, resolved by searching upward from
/// the working directory. That produced two different libraries on one machine — the repo
/// copy and the one under <c>bin/</c> — so which materials you saw depended on how the app
/// was launched, edits could be wiped by a rebuild, and one person's library could reach the
/// whole team through git. <c>assets/materials.json</c> is now read-only SEED data, copied in
/// once on first run.
/// </para>
/// </summary>
public static class MaterialPresetsLoader
{
    private const string SeedRelativePath = "assets/materials.json";

    private static readonly string UserDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer");

    private static readonly string UserPath = Path.Combine(UserDir, "materials.json");

    /// <summary>Last save error, or null when the last save succeeded. Surfaced by the UI.</summary>
    public static string? LastSaveError { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Resolves <c>assets/materials.json</c> via <see cref="AssetPaths"/> so the
    /// loader finds the repo copy even when <c>assets/</c> exists beside the exe
    /// (cells/krl deploy) but <c>materials.json</c> has not been copied there yet.
    /// </summary>
    /// <summary>User library path; seeded from the repo asset on first run.</summary>
    private static string ResolvePath()
    {
        if (!File.Exists(UserPath))
        {
            var seed = AssetPaths.Resolve(SeedRelativePath);
            if (File.Exists(seed))
            {
                try
                {
                    Directory.CreateDirectory(UserDir);
                    File.Copy(seed, UserPath);
                }
                catch { /* fall through — Load() will simply return empty */ }
            }
        }
        return UserPath;
    }

    public static List<MaterialPreset> Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<MaterialPreset>>(
                File.ReadAllText(path), Options) ?? [];
        }
        catch { return []; }
    }

    public static void Save(IEnumerable<MaterialPreset> presets)
    {
        var path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(presets.ToList(), Options));
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            // Was a bare catch — a failed save looked identical to a successful one.
            LastSaveError = ex.Message;
            System.Console.Error.WriteLine($"[materials] save failed: {path}: {ex.Message}");
        }
    }
}
