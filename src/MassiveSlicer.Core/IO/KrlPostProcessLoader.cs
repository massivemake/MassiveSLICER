using System.Text.Json;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Persists KRL post-process settings to <c>assets/krl_postprocess.json</c>.</summary>
public static class KrlPostProcessLoader
{
    private const string RelativePath = "assets/krl_postprocess.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    private static string ResolvePath() => AssetPaths.Resolve(RelativePath);

    public static KrlPostProcessSettings Load()
    {
        var path = ResolvePath();
        if (!File.Exists(path)) return new KrlPostProcessSettings();
        try
        {
            return JsonSerializer.Deserialize<KrlPostProcessSettings>(File.ReadAllText(path), Options)
                   ?? new KrlPostProcessSettings();
        }
        catch { return new KrlPostProcessSettings(); }
    }

    public static void Save(KrlPostProcessSettings settings)
    {
        var path = ResolvePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch { }
    }
}