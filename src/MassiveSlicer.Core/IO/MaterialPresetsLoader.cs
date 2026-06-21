using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Persists the shared material preset library as JSON inside the project's
/// <c>assets/</c> folder so that every change is tracked by git.
///
/// Path: <c>assets/materials.json</c>, resolved via <see cref="AssetPaths"/>
/// (exe dir, cwd, and parent folders).
///
/// Committing this file captures the full history of material additions and
/// edits. Use <c>git log assets/materials.json</c> to see the change history.
/// </summary>
public static class MaterialPresetsLoader
{
    private const string RelativePath = "assets/materials.json";

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
    private static string ResolvePath() => AssetPaths.Resolve(RelativePath);

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
        }
        catch { }
    }
}
