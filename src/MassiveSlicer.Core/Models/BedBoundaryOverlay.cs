namespace MassiveSlicer.Core.Models;

/// <summary>
/// Print-area overlay the viewport should draw for the active KRL base.
/// <see cref="Diameter"/> &gt; 0 means a polar/circular rotary grid; 0 means a rectangle.
/// </summary>
public readonly record struct BedBoundaryOverlaySpec(
    float Width,
    float Depth,
    float Diameter,
    Float3 GridCorner,
    Float3 Datum)
{
    public bool IsRectangular => Diameter <= 0f;
}

/// <summary>
/// Chooses circular vs rectangular print-bed overlay from the active KRL base.
/// Does not touch robot motion, IK, or KRL export — viewport drawing only.
/// </summary>
public static class BedBoundaryOverlay
{
    /// <summary>LFAM 3 controller convention: <c>BASE_DATA[6]</c> is the lower heated bed.</summary>
    public const int Lfam3HeatedBaseIndex = 6;

    public const string RectangularOverlay = "rectangular";
    public const string PolarOverlay = "polar";

    /// <summary>
    /// True when the active KRL base is the lower heated / rectangular bed.
    /// </summary>
    /// <summary>
    /// Print-area overlay is drawn whenever Bed grid is on, including Arctic / Preview / Body.
    /// Only the 2D slice viewer suppresses it (and the world ground grid still skips Arctic).
    /// </summary>
    public static bool ShouldDrawOverlay(bool showBedGrid, bool slicePlaneViewerActive)
        => showBedGrid && !slicePlaneViewerActive;

    public static bool IsHeatedPrintBase(
        int krlBaseIndex,
        IReadOnlyList<KrlBaseEntry>? bases,
        BedCellConfig? bed = null)
    {
        var entry = FindBase(bases, krlBaseIndex);
        if (entry is not null)
        {
            if (IsExplicitRectangular(entry)) return true;
            if (IsExplicitPolar(entry)) return false;
            if (NameLooksHeated(entry.Name)) return true;
        }

        // Infer BASE #6 only on a rotary cell so a flat cell's unused index 6 stays rectangular
        // via diameter == 0, and is not treated as a special heated switch.
        return krlBaseIndex == Lfam3HeatedBaseIndex && bed?.Diameter is > 0f;
    }

    /// <summary>
    /// Resolves overlay geometry for the active base.
    /// Heated size fallback (first match wins):
    /// <c>bed.heatedWidth/heatedDepth</c> → live width/depth → heated-mesh AABB →
    /// <c>bed.width/depth</c> → square of <c>bed.diameter</c>.
    /// LFAM 3 ships 1800×1800 in <c>bed.width/depth</c> and no dedicated heated size fields.
    /// </summary>
    public static BedBoundaryOverlaySpec Resolve(
        BedCellConfig bed,
        Float3 robrootWorld,
        int krlBaseIndex,
        IReadOnlyList<KrlBaseEntry>? bases,
        float? liveWidth = null,
        float? liveDepth = null,
        float? liveDiameter = null,
        (float Width, float Depth)? heatedMeshSize = null)
    {
        var gridCorner = bed.VisualGridCorner(robrootWorld);
        var gridDatum = bed.HasVisualShift && bed.GridOrigin is null
            ? gridCorner
            : new Float3(bed.Origin.X, bed.Origin.Y, gridCorner.Z);

        if (IsHeatedPrintBase(krlBaseIndex, bases, bed))
        {
            var (width, depth, _) = ResolveHeatedSize(bed, liveWidth, liveDepth, heatedMeshSize);
            var corner = HeatedGridCorner(bed, robrootWorld, width, depth);
            var datum = new Float3(bed.Origin.X, bed.Origin.Y, corner.Z);
            return new BedBoundaryOverlaySpec(width, depth, Diameter: 0f, corner, datum);
        }

        float widthR = liveWidth ?? bed.Width;
        float depthR = liveDepth ?? bed.Depth;
        float diameter = liveDiameter ?? bed.Diameter ?? 0f;
        return new BedBoundaryOverlaySpec(widthR, depthR, diameter, gridCorner, gridDatum);
    }

    /// <summary>Returns the heated print rectangle and a short label of which source supplied it.</summary>
    public static (float Width, float Depth, string Source) ResolveHeatedSize(
        BedCellConfig bed,
        float? liveWidth = null,
        float? liveDepth = null,
        (float Width, float Depth)? heatedMeshSize = null)
    {
        if (bed.HeatedWidth is > 0f && bed.HeatedDepth is > 0f)
            return (bed.HeatedWidth.Value, bed.HeatedDepth.Value, "bed.heatedWidth/heatedDepth");

        if (liveWidth is > 0f && liveDepth is > 0f)
            return (liveWidth.Value, liveDepth.Value, "live bed.width/depth");

        if (heatedMeshSize is { Width: > 0f, Depth: > 0f } mesh)
            return (mesh.Width, mesh.Depth, "heated-bed mesh AABB");

        if (bed.Width > 0f && bed.Depth > 0f)
            return (bed.Width, bed.Depth, "bed.width/depth");

        if (bed.Diameter is float d && d > 0f)
            return (d, d, "bed.diameter square fallback");

        return (1800f, 1800f, "default 1800 mm square");
    }

    public static Float3 HeatedGridCorner(BedCellConfig bed, Float3 robrootWorld, float width, float depth)
    {
        if (SameSize(width, bed.Width) && SameSize(depth, bed.Depth))
            return bed.VisualGridCorner(robrootWorld);

        return new Float3(
            bed.Origin.X - width * 0.5f,
            bed.Origin.Y - depth * 0.5f,
            bed.VisualGridCorner(robrootWorld).Z);
    }

    private static KrlBaseEntry? FindBase(IReadOnlyList<KrlBaseEntry>? bases, int index)
    {
        if (bases is null || index <= 0) return null;
        foreach (var b in bases)
            if (b.Index == index) return b;
        return null;
    }

    private static bool IsExplicitRectangular(KrlBaseEntry entry)
        => string.Equals(entry.Overlay, RectangularOverlay, StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitPolar(KrlBaseEntry entry)
        => string.Equals(entry.Overlay, PolarOverlay, StringComparison.OrdinalIgnoreCase);

    internal static bool NameLooksHeated(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("HEATED", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LOWER BED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameSize(float a, float b) => MathF.Abs(a - b) < 0.5f;
}
