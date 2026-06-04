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

    // -- Per-cell position data (stored inside the cell JSON) -----------------

    private static readonly JsonSerializerOptions WriteOptions = new(Options)
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Reads the named home positions and selected default from the cell JSON at
    /// <paramref name="cellPath"/>.
    /// </summary>
    public static CellPositionData LoadPositionData(string cellPath)
    {
        try
        {
            var cell = Load(cellPath);
            return new CellPositionData
            {
                Default   = cell.Robot.DefaultHomePosition,
                Positions = [.. cell.Robot.HomePositions],
            };
        }
        catch { return new CellPositionData(); }
    }

    /// <summary>
    /// Writes updated home positions and selected default back into the cell JSON at
    /// <paramref name="cellPath"/>, preserving all other cell settings.
    /// </summary>
    public static void SavePositionData(string cellPath, CellPositionData data)
    {
        try
        {
            var cell    = Load(cellPath);
            var updated = cell with
            {
                Robot = cell.Robot with
                {
                    HomePositions       = data.Positions,
                    DefaultHomePosition = data.Default,
                },
            };
            File.WriteAllText(cellPath, JsonSerializer.Serialize(updated, WriteOptions));
        }
        catch { /* non-fatal */ }
    }
}
