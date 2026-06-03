using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Persists the shared material preset library as JSON inside the project's
/// <c>assets/</c> folder so that every change is tracked by git.
///
/// Path (resolved relative to the process working directory, which is the
/// repo root when running via <c>dotnet run</c>):
///   <c>assets/materials.json</c>
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
    /// Resolves the materials file path. Prefers <c>assets/materials.json</c>
    /// relative to the current working directory (repo root). Falls back to
    /// the executable's directory for published builds.
    /// </summary>
    private static string ResolvePath()
    {
        var cwdPath = Path.GetFullPath(RelativePath);
        if (File.Exists(cwdPath) || Directory.Exists(Path.GetDirectoryName(cwdPath)!))
            return cwdPath;

        // Fallback: walk up from the executable to find the assets folder.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, RelativePath);
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        return cwdPath; // best effort
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
        }
        catch { }
    }
}
