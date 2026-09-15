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
    Float3 Datum,
    string Source = "")
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
    /// <c>bed.heatedWidth/heatedDepth</c> → heated-mesh AABB →
    /// <c>bed.width/depth</c> → square of <c>bed.diameter</c>.
    /// When <paramref name="heatedBed"/> is set, the rectangle is placed on that
    /// plate (mesh AABB only if it sits on the pose; otherwise ROBROOT + basePos)
    /// — never on <see cref="BedCellConfig.Origin"/> /
    /// <see cref="BedCellConfig.VisualGridCorner"/> while a heated pose exists.
    /// </summary>
    public static BedBoundaryOverlaySpec Resolve(
        BedCellConfig bed,
        Float3 robrootWorld,
        int krlBaseIndex,
        IReadOnlyList<KrlBaseEntry>? bases,
        float? liveWidth = null,
        float? liveDepth = null,
        float? liveDiameter = null,
        (float Width, float Depth)? heatedMeshSize = null,
        HeatedBedCellConfig? heatedBed = null,
        (Float3 Min, Float3 Max)? heatedMeshAabb = null,
        Float3? liveHeatedOrigin = null)
    {
        var gridCorner = bed.VisualGridCorner(robrootWorld);
        var gridDatum = bed.HasVisualShift && bed.GridOrigin is null
            ? gridCorner
            : new Float3(bed.Origin.X, bed.Origin.Y, gridCorner.Z);

        if (IsHeatedPrintBase(krlBaseIndex, bases, bed))
            return ResolveHeatedOverlay(
                bed, robrootWorld, liveWidth, liveDepth, heatedMeshSize,
                heatedBed, heatedMeshAabb, liveHeatedOrigin);

        float widthR = liveWidth ?? bed.Width;
        float depthR = liveDepth ?? bed.Depth;
        float diameter = liveDiameter ?? bed.Diameter ?? 0f;
        return new BedBoundaryOverlaySpec(widthR, depthR, diameter, gridCorner, gridDatum, "rotary/visual-grid");
    }

    static BedBoundaryOverlaySpec ResolveHeatedOverlay(
        BedCellConfig bed,
        Float3 robrootWorld,
        float? liveWidth,
        float? liveDepth,
        (float Width, float Depth)? heatedMeshSize,
        HeatedBedCellConfig? heatedBed,
        (Float3 Min, Float3 Max)? heatedMeshAabb,
        Float3? liveHeatedOrigin)
    {
        // JSON pose first: a live node Row3 can still be identity before world
        // matrices land. liveHeatedOrigin is only the no-JSON fallback.
        var pose = heatedBed?.WorldOrigin(robrootWorld) ?? liveHeatedOrigin;

        // AABB-first (fefe94e) placed the square on the rotary when the box was
        // local-only or the print-area mesh. Accept AABB only when it sits on
        // the heated pose — otherwise it must not override WorldOrigin.
        if (TryAabb(heatedMeshAabb, out var aabbMin, out var aabbMax)
            && AabbBelongsToHeatedPose(aabbMin, aabbMax, pose, bed))
            return FromWorldAabb(aabbMin, aabbMax, "aabb");

        var (width, depth, _) = ResolveHeatedSize(
            bed,
            liveWidth: pose is null ? liveWidth : null,
            liveDepth: pose is null ? liveDepth : null,
            heatedMeshSize);

        if (pose is { } origin)
        {
            var corner = new Float3(origin.X - width * 0.5f, origin.Y - depth * 0.5f, origin.Z);
            var src = heatedBed is not null ? "heatedBed.WorldOrigin" : "HeatedBed node";
            return new BedBoundaryOverlaySpec(width, depth, Diameter: 0f, corner, origin, src);
        }

        var fallbackCorner = HeatedGridCorner(bed, robrootWorld, width, depth);
        var fallbackDatum = new Float3(bed.Origin.X, bed.Origin.Y, fallbackCorner.Z);
        return new BedBoundaryOverlaySpec(
            width, depth, Diameter: 0f, fallbackCorner, fallbackDatum, "fallback");
    }

    static BedBoundaryOverlaySpec FromWorldAabb(Float3 min, Float3 max, string source)
    {
        float width = max.X - min.X;
        float depth = max.Y - min.Y;
        var corner = new Float3(min.X, min.Y, max.Z);
        var datum = new Float3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, max.Z);
        return new BedBoundaryOverlaySpec(width, depth, Diameter: 0f, corner, datum, source);
    }

    /// <summary>
    /// True when the AABB centre sits on the heated pose, not the rotary print-area.
    /// </summary>
    internal static bool AabbBelongsToHeatedPose(
        Float3 min, Float3 max, Float3? heatedPose, BedCellConfig bed)
    {
        var center = new Float3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f);
        if (heatedPose is { } pose)
            return DistXy(center, pose) <= 1200f && MathF.Abs(center.Z - pose.Z) <= 400f;

        // No pose to check against — refuse an AABB that is clearly the rotary rectangle.
        return DistXy(center, bed.Origin) > 800f && MathF.Abs(center.Z - bed.Origin.Z) > 200f;
    }

    static float DistXy(Float3 a, Float3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    static bool TryAabb((Float3 Min, Float3 Max)? aabb, out Float3 min, out Float3 max)
    {
        min = default;
        max = default;
        if (aabb is not { } box) return false;
        if (box.Max.X - box.Min.X <= 1f || box.Max.Y - box.Min.Y <= 1f) return false;
        min = box.Min;
        max = box.Max;
        return true;
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
