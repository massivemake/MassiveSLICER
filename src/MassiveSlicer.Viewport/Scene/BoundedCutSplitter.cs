using System.Linq;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// A "restricted" (non-Infinite) Cut: unlike <see cref="PlanarMeshSplitter"/>'s unbounded plane,
/// this only cuts the mesh within a rectangular footprint (±halfSizeX along <c>tangentU</c>,
/// ±halfSizeY along <c>tangentV</c>, centered at <c>planePoint</c>) — still a full through-cut
/// wherever that footprint overlaps the mesh (this is a bounded SLAB, not a partial-depth pocket:
/// you can't cut only slightly into a wall), but outside the footprint the mesh is left completely
/// uncut and stays connected to whichever side it naturally continues into. Whether that leaves
/// the model as one bridged piece or splits it into independent pieces falls out naturally from
/// the mesh's own shape relative to the rectangle.
/// </summary>
public static class BoundedCutSplitter
{
    /// <summary>Returns the final, fully-resolved independent pieces (already run through
    /// connectivity analysis — the caller doesn't need to call <see cref="MeshIslands"/> again),
    /// or null if the rectangle doesn't overlap this mesh at all, or the plane doesn't actually
    /// cross the mesh within the footprint (nothing to cut).</summary>
    public static List<MeshData>? Split(
        MeshData mesh, Vector3 planePoint, Vector3 planeNormal,
        Vector3 tangentU, Vector3 tangentV, float halfSizeX, float halfSizeY)
    {
        // Clip down to the slab (the rectangle extruded infinitely along planeNormal) via 4
        // sequential half-space clips — valid because a rectangular prism is convex, so
        // intersecting with each wall in turn converges on the same result regardless of order.
        // Whatever gets clipped AWAY at each wall was never inside the cut's footprint, so it's
        // kept raw (uncapped) and reunited with the final result unchanged — it must stay exactly
        // as it was, not gain a face of its own the way a real cut does.
        var outside = new List<MeshData>();
        var inside = mesh;
        (Vector3 dir, float halfSize, float sign)[] walls =
        [
            (tangentU, halfSizeX, 1f), (tangentU, halfSizeX, -1f),
            (tangentV, halfSizeY, 1f), (tangentV, halfSizeY, -1f),
        ];
        foreach (var (dir, halfSize, sign) in walls)
        {
            if (inside.Positions.Length == 0) break;
            var wallPoint  = planePoint + dir * (halfSize * sign);
            var wallNormal = dir * -sign;
            var (kept, discarded) = PlanarMeshSplitter.ClipUncapped(inside, wallPoint, wallNormal);
            inside = kept;
            if (discarded.Positions.Length > 0) outside.Add(discarded);
        }

        if (inside.Positions.Length == 0) return null; // rectangle doesn't overlap this mesh at all

        var cut = PlanarMeshSplitter.Split(inside, planePoint, planeNormal);
        bool crosses = cut.Positive.Positions.Length > 0 && cut.Negative.Positions.Length > 0;
        if (!crosses) return null; // footprint overlaps the mesh, but the plane doesn't cross it there

        // Resolve each side's OWN connectivity first (loose epsilon — safe here, since only one
        // side's geometry, including its own cap, is present; no opposite side to be mistaken
        // for). Only after that do the different pieces get merged against each other — Positive
        // and Negative are never compared directly (see MeshIslands.MergeFragments), only ever
        // bridged via genuinely untouched Outside material.
        List<(MeshData, MeshIslands.FragmentSide)> fragments =
        [
            .. MeshIslands.Split(cut.Positive).Select(m => (m, MeshIslands.FragmentSide.Positive)),
            .. MeshIslands.Split(cut.Negative).Select(m => (m, MeshIslands.FragmentSide.Negative)),
            .. outside.Select(m => (m, MeshIslands.FragmentSide.Outside)),
        ];
        return MeshIslands.MergeFragments(fragments);
    }
}
