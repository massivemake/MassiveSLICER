using System.Numerics;
using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Core.Models;

/// <summary>
/// World-space print surface for import, cell-swap remap, drag floor, and Drop to Plate.
/// Dual-bed cells (LFAM 3): rotary platter vs lower heated plate (BASE #6).
/// A rectangular cell (LFAM 1/2) maps onto the heated plate, not the turntable.
/// </summary>
public static class PrintSurface
{
    public static Float3 Center(CellConfig cell, bool heated)
    {
        if (heated && cell.HeatedBed is { } hb)
            return hb.WorldOrigin(cell.Robot.WorldPosition);
        return cell.Bed.ImportSurfaceCenter(cell.Robot.WorldPosition);
    }

    /// <summary>
    /// True when user content should land on <paramref name="dest"/>'s heated plate
    /// because <paramref name="source"/> was a rectangular print bed.
    /// </summary>
    public static bool AnalogIsHeated(CellConfig? source, CellConfig dest)
        => dest.HeatedBed is not null && source is not null && !source.Bed.IsRotaryPrintBed;

    public static int AnalogKrlBaseIndex(CellConfig dest, bool heated, int fallback)
    {
        if (heated && dest.HeatedBed is { KrlBaseIndex: > 0 } hb)
            return hb.KrlBaseIndex;
        return fallback;
    }

    public static bool IsHeatedBase(CellConfig cell, int krlBaseIndex)
        => BedBoundaryOverlay.IsHeatedPrintBase(krlBaseIndex, cell.KrlBases, cell.Bed);

    /// <summary>
    /// World origin + KUKA BASE_DATA offset for Send / SRC. BASE #6 on LFAM 3 is
    /// the heated plate (ROBROOT + heatedBed.basePos), not rotary <c>bed.origin</c>.
    /// </summary>
    public readonly record struct KrlExportFrame(Float3 Origin, Float3 BaseData, float SliceWorldZ);

    public static KrlExportFrame ForKrlBase(CellConfig cell, int krlBaseIndex)
    {
        if (IsHeatedBase(cell, krlBaseIndex) && cell.HeatedBed is { } hb)
        {
            var origin = hb.WorldOrigin(cell.Robot.WorldPosition);
            var bp = hb.BasePos;
            var baseData = new Float3(
                bp.Length > 0 ? bp[0] : 0f,
                bp.Length > 1 ? bp[1] : 0f,
                bp.Length > 2 ? bp[2] : 0f);
            return new KrlExportFrame(origin, baseData, origin.Z);
        }

        var bed = cell.Bed;
        return new KrlExportFrame(bed.Origin, bed.BaseData, bed.Origin.Z);
    }

    /// <summary>
    /// World point for KRL <c>{X 0, Y 0, Z 0}</c> on the selected base.
    /// XY stays on the cell bed marker. On LFAM 3 that marker is what already
    /// lines an imported program up over the heated plate. A heated base
    /// replaces only Z with the plate height, so the path is not left up at
    /// the rotary table.
    /// </summary>
    public static Float3 ImportOrigin(CellConfig cell, int krlBaseIndex)
    {
        var marker = cell.Bed.BaseMarkerWorld(cell.Robot.WorldPosition);
        if (IsHeatedBase(cell, krlBaseIndex) && cell.HeatedBed is not null)
        {
            var frame = ForKrlBase(cell, krlBaseIndex);
            var rp = cell.Robot.WorldPosition;
            var world = KrlExporter.ImportWorldOffset(
                new Vector3(rp.X, rp.Y, rp.Z),
                new Vector3(frame.BaseData.X, frame.BaseData.Y, frame.BaseData.Z),
                frame.SliceWorldZ);
            return new Float3(marker.X, marker.Y, world.Z);
        }

        return marker;
    }
}
