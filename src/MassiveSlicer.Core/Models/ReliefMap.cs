using System;

namespace MassiveSlicer.Core.Models;

/// <summary>
/// A grayscale relief/heightmap sampled onto a regular grid and placed on the bed in world mm.
/// Decode-agnostic (no image dependency) — the App layer decodes a PNG/JPG into <see cref="Samples"/>.
/// Mirrors the heightfield shape used by <c>BedScanAnalyzer</c>.
/// <para>
/// Convention: white (sample = 1) sits at <see cref="ReferencePlaneZ"/> (least material removed);
/// black (0) is carved <see cref="HeightScaleMm"/> deeper. This is the final detailed surface a
/// milling toolpath carves into a blank (and, later, the surface the additive blank must envelope).
/// </para>
/// </summary>
public sealed class ReliefMap
{
    /// <summary>Normalized samples 0..1, row-major (<c>row*Cols + col</c>), row 0 = bottom (at <see cref="OriginY"/>).</summary>
    public required float[] Samples { get; init; }
    public required int Cols { get; init; }
    public required int Rows { get; init; }

    /// <summary>World X of column 0 (left edge of the footprint), mm.</summary>
    public required float OriginX { get; init; }
    /// <summary>World Y of row 0 (bottom edge of the footprint), mm.</summary>
    public required float OriginY { get; init; }
    /// <summary>Footprint extent in X (mm) — the image width maps onto this.</summary>
    public required float WidthMm { get; init; }
    /// <summary>Footprint extent in Y (mm) — the image height maps onto this.</summary>
    public required float LengthMm { get; init; }

    /// <summary>Relief depth (mm) between black and white.</summary>
    public required float HeightScaleMm { get; init; }
    /// <summary>Flip black/white (so black becomes the high surface).</summary>
    public bool Invert { get; init; }
    /// <summary>Nominal top-surface Z (world mm) that white maps to.</summary>
    public required float ReferencePlaneZ { get; init; }

    public float CellX => Cols > 1 ? WidthMm  / (Cols - 1) : WidthMm;
    public float CellY => Rows > 1 ? LengthMm / (Rows - 1) : LengthMm;

    /// <summary>Normalized relief 0..1 at a grid cell (respects <see cref="Invert"/>); 1 = high surface.</summary>
    private float Norm(int col, int row)
    {
        float s = Samples[row * Cols + col];
        return Invert ? 1f - s : s;
    }

    /// <summary>Carved target surface Z (world mm) at a grid cell. White → ReferencePlaneZ, black → ReferencePlaneZ − HeightScaleMm.</summary>
    public float SurfaceZAt(int col, int row)
        => ReferencePlaneZ - HeightScaleMm + Norm(col, row) * HeightScaleMm;

    /// <summary>Bilinearly-sampled carved surface Z at world (x,y), mm. Returns <see cref="float.NaN"/> outside the footprint.</summary>
    public float SampleSurfaceZ(float x, float y)
    {
        if (Cols < 2 || Rows < 2) return float.NaN;
        float fx = (x - OriginX) / MathF.Max(WidthMm,  1e-6f) * (Cols - 1);
        float fy = (y - OriginY) / MathF.Max(LengthMm, 1e-6f) * (Rows - 1);
        if (fx < 0f || fy < 0f || fx > Cols - 1 || fy > Rows - 1) return float.NaN;

        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        int x1 = Math.Min(x0 + 1, Cols - 1), y1 = Math.Min(y0 + 1, Rows - 1);
        float tx = fx - x0, ty = fy - y0;

        float zx0 = SurfaceZAt(x0, y0) + (SurfaceZAt(x1, y0) - SurfaceZAt(x0, y0)) * tx;
        float zx1 = SurfaceZAt(x0, y1) + (SurfaceZAt(x1, y1) - SurfaceZAt(x0, y1)) * tx;
        return zx0 + (zx1 - zx0) * ty;
    }
}
