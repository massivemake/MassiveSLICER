using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Computes world-space projection regions for geometry-based contact shadows.
/// </summary>
public static class ContactShadowBuilder
{
    public const float DefaultPaddingMm     = 440f;
    public const float FloorSurfaceOffset   = 0.6f;
    /// <summary>Only geometry within this height above the floor plane contributes to the silhouette.</summary>
    public const float ContactBandMm        = 220f;
    private const float FloorBucketMm       = 0.5f;

    public readonly record struct ShadowProjection(
        float MinX, float MinY, float MaxX, float MaxY,
        float FloorZ, float MaxZ);

    /// <summary>
    /// Builds one shadow pass per contact-height group (ground under bed rim, bed top, etc.).
    /// </summary>
    public static List<ShadowProjection> BuildProjections(
        SceneNode sceneRoot,
        float paddingMm = DefaultPaddingMm,
        float floorOffsetMm = FloorSurfaceOffset)
    {
        var buckets = new Dictionary<int, (float FloorZ, Vector3 Min, Vector3 Max, float MaxZ)>();

        foreach (var child in sceneRoot.Children)
        {
            if (!ShouldCastShadow(child))
                continue;

            if (!SceneBounds.TryComputeSubtreeWorldAabb(child, out var cMin, out var cMax))
                continue;

            float floorTop = ContactZ(cMin, cMax, floorOffsetMm);
            AddToBucket(buckets, floorTop, cMin, cMax);

            // Raised plates (print bed) also cast a rim shadow on the ground below.
            if (IsThinGroundPlate(cMin, cMax))
            {
                float floorBottom = cMin.Z + floorOffsetMm;
                if (MathF.Abs(floorBottom - floorTop) > FloorBucketMm)
                    AddToBucket(buckets, floorBottom, cMin, cMax);
            }
        }

        if (buckets.Count == 0)
            return [];

        return buckets.Values
            .OrderBy(b => b.FloorZ)
            .Select(b => new ShadowProjection(
                b.Min.X - paddingMm, b.Min.Y - paddingMm,
                b.Max.X + paddingMm, b.Max.Y + paddingMm,
                b.FloorZ, b.MaxZ))
            .ToList();
    }

    public static ShadowProjection? BuildProjection(SceneNode sceneRoot,
        float paddingMm = DefaultPaddingMm,
        float floorOffsetMm = FloorSurfaceOffset)
    {
        var passes = BuildProjections(sceneRoot, paddingMm, floorOffsetMm);
        return passes.Count > 0 ? passes[0] : null;
    }

    public static bool ShouldCastShadow(SceneNode root)
    {
        if (!root.Visible || root.Overlay)
            return false;

        if (root.Name.Contains("Toolpath", StringComparison.OrdinalIgnoreCase))
            return false;

        // Robot GLBs include a KR base collar at the mount height (e.g. z=1000 on LFAM 2).
        // The structural pedestal is the separate booster frame at ground — skip the armature.
        if (IsRobotAssemblyRoot(root))
            return false;

        foreach (var n in root.SelfAndDescendants())
        {
            if (!n.Visible) continue;
            if (n.Mesh?.PickingData is not null || n.PendingMesh is not null)
                return true;
        }

        return false;
    }

    public static bool ShouldCastSilhouette(SceneNode root, float floorZ, float floorOffsetMm = FloorSurfaceOffset)
    {
        if (!ShouldCastShadow(root))
            return false;

        if (!SceneBounds.TryComputeSubtreeWorldAabb(root, out var min, out var max))
            return false;

        float top    = ContactZ(min, max, floorOffsetMm);
        float bottom = min.Z + floorOffsetMm;
        return MathF.Abs(top - floorZ) <= FloorBucketMm
            || (IsThinGroundPlate(min, max) && MathF.Abs(bottom - floorZ) <= FloorBucketMm);
    }

    private static void AddToBucket(
        Dictionary<int, (float FloorZ, Vector3 Min, Vector3 Max, float MaxZ)> buckets,
        float floorZ, Vector3 cMin, Vector3 cMax)
    {
        int key = (int)MathF.Round(floorZ / FloorBucketMm);

        if (!buckets.TryGetValue(key, out var bucket))
        {
            buckets[key] = (floorZ, cMin, cMax, cMax.Z);
            return;
        }

        buckets[key] = (
            bucket.FloorZ,
            Vector3.ComponentMin(bucket.Min, cMin),
            Vector3.ComponentMax(bucket.Max, cMax),
            MathF.Max(bucket.MaxZ, cMax.Z));
    }

    private static bool IsThinGroundPlate(Vector3 min, Vector3 max)
    {
        float height    = max.Z - min.Z;
        float footprint = MathF.Max(max.X - min.X, max.Y - min.Y);
        return height < 80f && footprint > 400f;
    }

    private static float ContactZ(Vector3 min, Vector3 max, float floorOffsetMm)
    {
        float height    = max.Z - min.Z;
        float footprint = MathF.Max(max.X - min.X, max.Y - min.Y);
        bool thinGround = height < 80f && footprint > 400f;
        return (thinGround ? max.Z : min.Z) + floorOffsetMm;
    }

    private static bool IsRobotAssemblyRoot(SceneNode root)
        => root.Name.EndsWith("_Robot", StringComparison.Ordinal)
        || root.FindDescendant("joint_1") is not null;
}