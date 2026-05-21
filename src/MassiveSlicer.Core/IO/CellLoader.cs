using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Loads <see cref="CellConfig"/> instances from JSON files in <c>assets/cells/</c>.</summary>
public static class CellLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive  = true,
        ReadCommentHandling          = JsonCommentHandling.Skip,
        AllowTrailingCommas          = true,
        DefaultIgnoreCondition       = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Deserialises a cell JSON file. Throws on malformed input.</summary>
    public static CellConfig Load(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CellConfig>(json, Options)
            ?? throw new InvalidDataException($"Cell file is empty: {path}");
    }

    /// <summary>Returns paths of all <c>*.json</c> files under the given directory (recursive).</summary>
    public static IEnumerable<string> FindAll(string directory = "assets/cells") =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            : [];
}
