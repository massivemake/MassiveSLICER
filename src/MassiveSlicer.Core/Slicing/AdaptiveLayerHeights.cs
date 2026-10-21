using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Computes adaptive slice positions from surface slope relative to the slicing axis.
///
/// Based on Wasserfall et al. "Adaptive Slicing for the FDM Process Revisited" (CASE 2017)
/// with Vojtech Bubnik's triangle-area error metric, as implemented in OrcaSlicer / PrusaSlicer.
///
/// Stairstepping is most visible on faces that lie nearly parallel to the slice plane.
/// Faces that run along the plane normal impose no constraint. Quality [0=finest, 1=coarsest]
/// maps the allowed surface deviation from MinLayerHeight to MaxLayerHeight.
/// Planar slicing passes <see cref="Vector3.UnitZ"/>; angled slicing passes the plane normal.
/// Both go through <see cref="ComputePositions"/>, so a vertical axis stays the old per-Z metric.
/// </summary>
public static class AdaptiveLayerHeights
{
    private readonly struct FaceSpan
    {
        public float TMin { get; init; }
        public float TMax { get; init; }
        public float NCos { get; init; }  // |n · axis|
        public float NSin { get; init; }  // length of n perpendicular to axis
    }

    /// <summary>
    /// Returns the list of world-Z values to slice at. Equivalent to
    /// <see cref="ComputePositions"/> along <see cref="Vector3.UnitZ"/>.
    /// </summary>
    public static float[] ComputeZPositions(
        IReadOnlyList<Vector3[]> meshes,
        float zMin, float zMax,
        float firstLayerHeight,
        float minLayerHeight, float maxLayerHeight,
        float qualityFactor)
        => ComputePositions(meshes, Vector3.UnitZ, zMin, zMax,
            firstLayerHeight, minLayerHeight, maxLayerHeight, qualityFactor);

    /// <summary>
    /// Returns signed distances along <paramref name="axis"/> at which to cut.
    /// <paramref name="axis"/> is the slice-plane normal (unit length). Positions start at
    /// <paramref name="tMin"/> + <paramref name="firstLayerHeight"/> and step by the slope metric.
    /// </summary>
    public static float[] ComputePositions(
        IReadOnlyList<Vector3[]> meshes,
        Vector3 axis,
        float tMin, float tMax,
        float firstLayerHeight,
        float minLayerHeight, float maxLayerHeight,
        float qualityFactor)
    {
        float len2 = axis.LengthSquared();
        if (len2 < 1e-8f) axis = Vector3.UnitZ;
        else if (MathF.Abs(len2 - 1f) > 1e-4f) axis /= MathF.Sqrt(len2);

        qualityFactor = Math.Clamp(qualityFactor, 0f, 1f);

        var faces = BuildFaces(meshes, axis);
        // Sort by span start so the range-scan loop below can exit early.
        faces.Sort((a, b) => a.TMin.CompareTo(b.TMin));

        var positions = new List<float>();
        float t = tMin + firstLayerHeight;
        int currentFacet = 0;

        while (t < tMax - 1e-4f)
        {
            positions.Add(t);
            float h = NextLayerHeight(faces, t, qualityFactor,
                minLayerHeight, maxLayerHeight, ref currentFacet);
            t += h;
        }

        return [.. positions];
    }

    private static float NextLayerHeight(
        List<FaceSpan> faces, float printT, float quality,
        float minH, float maxH, ref int currentFacet)
    {
        float height = maxH;

        // Map quality [0,1] → max allowed surface deviation [minH, maxH].
        // quality=0 → maxDev=minH  (tight tolerance, thin layers everywhere)
        // quality=1 → maxDev=maxH  (loose tolerance, thick layers where geometry allows)
        float maxDev = minH + quality * (maxH - minH);

        // Facets are sorted by TMin. Walk forward from currentFacet for any facet
        // that straddles printT (TMin < printT < TMax).
        int orderedId = currentFacet;
        bool firstHit = false;
        for (; orderedId < faces.Count; orderedId++)
        {
            var f = faces[orderedId];
            // Sorted list: once TMin ≥ printT nothing further can intersect from below.
            if (f.TMin >= printT) break;
            if (f.TMax > printT)
            {
                if (!firstHit) { firstHit = true; currentFacet = orderedId; }
                // Skip faces whose top just barely touches printT (degenerate contact).
                if (f.TMax < printT + 1e-4f) continue;
                float h = LayerHeightFromSlope(f, maxDev);
                if (h < height) height = h;
            }
        }

        height = MathF.Max(height, minH);

        // A face starting inside [printT, printT+height) might further restrict height.
        if (height > minH)
        {
            for (; orderedId < faces.Count; orderedId++)
            {
                var f = faces[orderedId];
                if (f.TMin >= printT + height) break;
                if (f.TMax < printT + 1e-4f) continue;

                float reducedH = LayerHeightFromSlope(f, maxDev);
                float tDiff    = f.TMin - printT;

                if (reducedH < tDiff)
                    height = tDiff;
                else if (reducedH < height)
                    height = reducedH;
            }
            height = MathF.Max(height, minH);
        }

        return height;
    }

    // Vojtech's triangle-area error metric (from OrcaSlicer/PrusaSlicer SlicingAdaptive.cpp).
    // Returns the maximum safe layer height that keeps surface deviation within maxDev.
    // The min with maxDev/0.184 caps the result for faces nearly parallel to the slice
    // plane, where the Vojtech term would otherwise collapse to zero.
    private static float LayerHeightFromSlope(in FaceSpan face, float maxDev)
    {
        float vojtech = face.NCos > 1e-5f
            ? 1.44f * maxDev * MathF.Sqrt(face.NSin / face.NCos)
            : float.MaxValue;
        return MathF.Min(maxDev / 0.184f, vojtech);
    }

    private static List<FaceSpan> BuildFaces(IReadOnlyList<Vector3[]> meshes, Vector3 axis)
    {
        var faces = new List<FaceSpan>();
        foreach (var verts in meshes)
        {
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                var v0 = verts[i]; var v1 = verts[i + 1]; var v2 = verts[i + 2];
                var n   = Vector3.Cross(v1 - v0, v2 - v0);
                float len = n.Length();
                if (len < 1e-8f) continue;
                n /= len;
                float t0 = Vector3.Dot(v0, axis);
                float t1 = Vector3.Dot(v1, axis);
                float t2 = Vector3.Dot(v2, axis);
                float nCos = MathF.Abs(Vector3.Dot(n, axis));
                var perp = n - axis * Vector3.Dot(n, axis);
                faces.Add(new FaceSpan
                {
                    TMin = MathF.Min(MathF.Min(t0, t1), t2),
                    TMax = MathF.Max(MathF.Max(t0, t1), t2),
                    NCos = nCos,
                    NSin = perp.Length(),
                });
            }
        }
        return faces;
    }
}
