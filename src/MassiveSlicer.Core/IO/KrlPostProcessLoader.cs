using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Factory file for KRL Post-Processing. Writes the git checkout
/// <c>assets/krl_postprocess.json</c> (not the bin/ copy a rebuild wipes).
/// </summary>
public static class KrlPostProcessLoader
{
    public const string RelativePath = "assets/krl_postprocess.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>Repo file when the checkout is visible; else the resolved asset.</summary>
    public static string FactoryPath()
    {
        var repo = AssetPaths.FindRepoRoot();
        if (repo is not null)
            return Path.GetFullPath(Path.Combine(repo, RelativePath));
        return AssetPaths.Resolve(RelativePath);
    }

    public static KrlPostProcessSettings Load()
    {
        foreach (var path in ReadCandidates())
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var s = JsonSerializer.Deserialize<KrlPostProcessSettings>(File.ReadAllText(path), Options);
                if (s is not null)
                    return s;
            }
            catch { /* try the next candidate */ }
        }
        return new KrlPostProcessSettings();
    }

    public static void Save(KrlPostProcessSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, Options);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in WriteCandidates())
        {
            if (!written.Add(path))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
    }

    private static IEnumerable<string> ReadCandidates()
    {
        yield return FactoryPath();
        yield return AssetPaths.Resolve(RelativePath);
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, RelativePath));
    }

    private static IEnumerable<string> WriteCandidates()
    {
        yield return FactoryPath();
        var bin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, RelativePath));
        yield return bin;
    }
}
