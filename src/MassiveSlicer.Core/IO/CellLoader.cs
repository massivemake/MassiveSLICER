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
    public static IEnumerable<string> FindAll(string directory = "assets/cells")
    {
        foreach (var cellsDir in CandidateCellDirectories(directory))
        {
            try
            {
                if (!Directory.Exists(cellsDir)) continue;
                var files = Directory.EnumerateFiles(cellsDir, "*.json", SearchOption.AllDirectories).ToList();
                if (files.Count > 0)
                    return files;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[cell] cannot enumerate '{cellsDir}': {ex.Message}");
            }
        }

        return [];
    }

    private static IEnumerable<string> CandidateCellDirectories(string fallbackRelative)
    {
        var preferred = CellPaths.PreferredCellsDirectory();
        if (preferred is not null)
            yield return preferred;

        foreach (var root in AssetPaths.SearchRoots())
        {
            var dir = Path.Combine(root, "assets", "cells");
            if (!string.Equals(Path.GetFullPath(dir), preferred, StringComparison.OrdinalIgnoreCase))
                yield return dir;
        }

        if (Directory.Exists(fallbackRelative))
            yield return Path.GetFullPath(fallbackRelative);
    }

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
                    Origin        = new Float3(x, y, z),
                    GridOrigin    = newGrid,
                    BaseData      = new Float3(x - rw.X, y - rw.Y, z - rw.Z),
                    Diameter      = diameter is > 0f ? diameter : cell.Bed.Diameter,
                    RotationSign  = rotationSign ?? cell.Bed.RotationSign,
                    // Preserve visualOffset — it is independent of measured rotary centre.
                    VisualOffset  = cell.Bed.VisualOffset,
                    VisualOrigin  = cell.Bed.VisualOrigin,
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

    public static bool SaveStandTransform(string cellPath, string standId,
        float[] position, float[] rotation, out string? error)
        => TryWrite(cellPath, cell => cell with
        {
            Stands = cell.Stands
                .Select(s => s.Id == standId
                    ? s with { Position = position, Rotation = rotation }
                    : s)
                .ToList(),
        }, out error);

    public static bool SaveRotaryBedTransform(string cellPath, float[] basePos, float[] baseAbc,
        out string? error)
    {
        error = null;
        try
        {
            var cell = Load(cellPath);
            if (cell.RotaryBed is not { } rb)
            {
                error = "cell has no rotary bed";
                return false;
            }

            return TryWrite(cellPath, c => c with
            {
                RotaryBed = rb with { BasePos = basePos, BaseAbc = baseAbc },
            }, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Sets the rotary bed's constant orientation offset (degrees about its vertical axis).</summary>
    public static bool SaveRotaryOrientation(string cellPath, float deg, out string? error)
    {
        error = null;
        try
        {
            var cell = Load(cellPath);
            if (cell.RotaryBed is not { } rb)
            {
                error = "cell has no rotary bed";
                return false;
            }
            return TryWrite(cellPath, c => c with
            {
                RotaryBed = rb with { OrientationOffsetDeg = deg },
            }, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool SaveToolDock(string cellPath, string toolName, ToolDockCellConfig dock,
        out string? error)
        => TryWrite(cellPath, cell => cell with
        {
            Tools = cell.Tools
                .Select(t => t.Name == toolName ? t with { Dock = dock } : t)
                .ToList(),
        }, out error);

    /// <summary>
    /// Persists dev-mode bed moves. Shifts <c>origin</c> + <c>gridOrigin</c> on LFAM 3,
    /// or updates <c>visualOffset</c> when the bed uses a BASE-relative visual shift.
    /// </summary>
    public static bool SaveBedDevTransform(string cellPath, float x, float y, float z,
        out string? error)
    {
        error = null;
        try
        {
            var cell = Load(cellPath);
            var bed  = cell.Bed;
            var rp   = cell.Robot.WorldPosition;

            if (bed.GridOrigin is { } grid)
            {
                float dx = x - bed.Origin.X;
                float dy = y - bed.Origin.Y;
                float dz = z - bed.Origin.Z;
                return TryWrite(cellPath, c => c with
                {
                    Bed = bed with
                    {
                        Origin       = new Float3(x, y, z),
                        GridOrigin   = new Float3(grid.X + dx, grid.Y + dy, grid.Z + dz),
                        VisualOrigin = null,
                    },
                }, out error);
            }

            if (bed.VisualOffset is not null)
            {
                var locked = bed.BaseMarkerWorld(rp);
                return TryWrite(cellPath, c => c with
                {
                    Bed = bed with
                    {
                        VisualOffset = new Float3(x - locked.X, y - locked.Y, bed.VisualOffset.Value.Z),
                        VisualOrigin = null,
                    },
                }, out error);
            }

            return TryWrite(cellPath, c => c with
            {
                Bed = bed with
                {
                    Origin       = new Float3(x, y, z),
                    VisualOrigin = null,
                },
            }, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryWrite(string cellPath, Func<CellConfig, CellConfig> mutate,
        out string? error)
    {
        error = null;
        var primary = Path.GetFullPath(cellPath);
        CellConfig updated;
        try
        {
            updated = mutate(Load(primary));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            System.Console.Error.WriteLine($"[cell] failed to read {primary}: {ex.Message}");
            return false;
        }

        bool primaryOk = false;
        foreach (var target in CellPaths.WriteTargetsFor(primary))
        {
            bool isPrimary = string.Equals(Path.GetFullPath(target), primary, StringComparison.OrdinalIgnoreCase);
            try
            {
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                WriteCellJson(target, updated);
                if (isPrimary) primaryOk = true;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[cell] failed to write {target}: {ex.Message}");
                if (isPrimary) error = ex.Message;
            }
        }

        if (!primaryOk && error is null)
            error = "primary cell write failed";
        return primaryOk;
    }

    static void WriteCellJson(string path, CellConfig cell)
        => File.WriteAllText(path, JsonSerializer.Serialize(cell, WriteOptions));
}
