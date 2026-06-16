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

    /// <summary>
    /// Writes a measured rotary-bed centre (world / ROBROOT mm) into the cell JSON at
    /// <paramref name="cellPath"/>: updates <c>bed.origin</c> to the new centre, shifts
    /// <c>bed.gridOrigin</c> by the same delta (keeping the print grid aligned), and
    /// recomputes <c>bed.baseData</c> as the centre relative to the robot's ROBROOT world
    /// position. All other cell settings are preserved.
    /// </summary>
    public static void SaveBedCenter(string cellPath, float x, float y, float z)
        => SaveBedCenter(cellPath, x, y, z, null, null);

    /// <summary>
    /// As <see cref="SaveBedCenter(string,float,float,float)"/>, and also sets <c>bed.diameter</c>
    /// when <paramref name="diameter"/> is non-null (null/≤0 leaves it unchanged) and
    /// <c>bed.rotationSign</c> when <paramref name="rotationSign"/> is non-null.
    /// </summary>
    public static void SaveBedCenter(string cellPath, float x, float y, float z,
        float? diameter, float? rotationSign)
    {
        try
        {
            var cell = Load(cellPath);
            var old  = cell.Bed.Origin;
            var rw   = cell.Robot.WorldPosition;
            float dx = x - old.X, dy = y - old.Y, dz = z - old.Z;

            var newGrid = cell.Bed.GridOrigin is { } g
                ? new Float3(g.X + dx, g.Y + dy, g.Z + dz)
                : (Float3?)null;

            var updated = cell with
            {
                Bed = cell.Bed with
                {
                    Origin       = new Float3(x, y, z),
                    GridOrigin   = newGrid,
                    BaseData     = new Float3(x - rw.X, y - rw.Y, z - rw.Z),
                    Diameter     = diameter is > 0f ? diameter : cell.Bed.Diameter,
                    RotationSign = rotationSign ?? cell.Bed.RotationSign,
                },
            };
            File.WriteAllText(cellPath, JsonSerializer.Serialize(updated, WriteOptions));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Saves the default camera view into the cell JSON at <paramref name="cellPath"/>,
    /// preserving all other cell settings. Applied on the next cell load (shared via the file).
    /// </summary>
    public static void SaveCameraView(string cellPath, CameraView view)
    {
        try
        {
            var cell    = Load(cellPath);
            var updated = cell with { View = view };
            File.WriteAllText(cellPath, JsonSerializer.Serialize(updated, WriteOptions));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Overwrites the TCP values for the tool matching <paramref name="krlIndex"/> in the
    /// cell JSON at <paramref name="cellPath"/>, preserving all other cell settings.
    /// </summary>
    public static void SaveToolTcp(string cellPath, int krlIndex,
        float x, float y, float z, float a, float b, float c)
    {
        try
        {
            var cell         = Load(cellPath);
            var updatedTools = cell.Tools
                .Select(t => t.KrlIndex == krlIndex
                    ? t with { TcpX = x, TcpY = y, TcpZ = z, TcpA = a, TcpB = b, TcpC = c }
                    : t)
                .ToList();
            var updated = cell with { Tools = updatedTools };
            File.WriteAllText(cellPath, JsonSerializer.Serialize(updated, WriteOptions));
        }
        catch { /* non-fatal */ }
    }
}
