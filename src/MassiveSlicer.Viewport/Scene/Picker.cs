using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// CPU-side ray-mesh intersection for scene picking.
/// Tests a world-space ray against all mesh nodes in the scene graph using
/// Moller-Trumbore in each node's local space.
/// </summary>
public static class Picker
{
    /// <summary>
    /// Finds the closest <see cref="SceneNode"/> under <paramref name="worldRay"/>.
    /// Only nodes whose <see cref="MeshRenderer.PickingData"/> is non-null are tested.
    /// </summary>
    /// <param name="worldRay">Ray in world space (direction must be normalised).</param>
    /// <param name="root">Root of the scene graph to search.</param>
    /// <param name="hitDistance">World-space distance to the closest hit, or <see cref="float.MaxValue"/>.</param>
    /// <returns>The hit node, or <c>null</c> if nothing was intersected.</returns>
    public static SceneNode? Pick(Ray worldRay, SceneNode root, out float hitDistance)
        => PickWhere(worldRay, root, _ => true, out hitDistance);

    /// <summary>
    /// Like <see cref="Pick"/> but only considers hits whose selectable root passes
    /// <paramref name="acceptSelectable"/>.
    /// </summary>
    public static SceneNode? PickWhere(
        Ray worldRay, SceneNode root, Func<SceneNode, bool> acceptSelectable, out float hitDistance)
    {
        hitDistance = float.MaxValue;
        PickTier bestTier = PickTier.Environment;
        SceneNode? closest = null;

        foreach (var node in root.SelfAndDescendants())
        {
            if (node.PickIgnore) continue;
            if (node.Mesh?.PickingData is not { } mesh) continue;
            if (FindSelectableRoot(node) is not { } selectable) continue;
            if (!acceptSelectable(selectable)) continue;

            Matrix4.Invert(node.WorldTransform, out var invWorld);
            var lo = TransformPoint(worldRay.Origin,    invWorld);
            var ld = TransformDir  (worldRay.Direction, invWorld);

            // Cheap AABB pre-reject before per-triangle Moller-Trumbore.
            var (bMin, bMax) = mesh.LocalBounds;
            if (!RayHitsAabb(lo, ld, bMin, bMax)) continue;

            // t in local space == t in world space (direction pre-scaled by invWorld)
            if (!Intersect(mesh, lo, ld, out float t)) continue;

            var tier = selectable.PickTier;
            if (tier < bestTier || (tier == bestTier && t < hitDistance))
            {
                bestTier    = tier;
                hitDistance = t;
                closest     = node;
            }
        }

        return closest;
    }

    /// <summary>
    /// Returns the nearest visible, selectable ancestor starting at <paramref name="node"/> — so scans
    /// nested under the rotary bed win over the bed root, and mesh leaves win over cell wrappers.
    /// </summary>
    public static SceneNode? FindSelectableRoot(SceneNode node)
    {
        var current = node;
        while (current is not null)
        {
            if (!current.Visible) return null;
            if (current.Selectable) return current;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Maps a mesh hit to the selectable object shown in the outliner.</summary>
    public static SceneNode? FindSelectableRoot(SceneNode node, SceneNode sceneRoot)
        => FindSelectableRoot(node);

    // -- Ray-AABB slab test ----------------------------------------------------

    private static bool RayHitsAabb(Vector3 ro, Vector3 rd, Vector3 min, Vector3 max)
    {
        float tMin = float.MinValue;
        float tMax = float.MaxValue;

        for (int i = 0; i < 3; i++)
        {
            float o  = i == 0 ? ro.X : i == 1 ? ro.Y : ro.Z;
            float d  = i == 0 ? rd.X : i == 1 ? rd.Y : rd.Z;
            float mn = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
            float mx = i == 0 ? max.X : i == 1 ? max.Y : max.Z;

            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < mn || o > mx) return false;
            }
            else
            {
                float t1 = (mn - o) / d;
                float t2 = (mx - o) / d;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
        }
        return tMax > 0f;
    }

    // -- Moller-Trumbore -------------------------------------------------------

    private static bool Intersect(MeshData mesh, Vector3 ro, Vector3 rd, out float tMin)
    {
        tMin = float.MaxValue;
        bool hit = false;
        var pos = mesh.Positions;

        // The "a" determinant below scales linearly with |rd|. A node whose WorldTransform bakes
        // in a non-unit scale (e.g. GLTF's native-metres-to-scene-mm ×1000 conversion) shrinks the
        // local-space rd by the inverse of that scale, so a fixed absolute epsilon silently rejects
        // real hits as "parallel". Scale the epsilon by |rd| so it stays correct at any node scale.
        float eps = BaseEps * rd.Length;

        if (mesh.Indices is { } idx)
        {
            for (int i = 0; i + 2 < idx.Length; i += 3)
                TestTri(pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]], ro, rd, eps, ref tMin, ref hit);
        }
        else
        {
            for (int i = 0; i + 2 < pos.Length; i += 3)
                TestTri(pos[i], pos[i + 1], pos[i + 2], ro, rd, eps, ref tMin, ref hit);
        }

        return hit;
    }

    private const float BaseEps = 1e-6f;

    private static void TestTri(
        Vector3 v0, Vector3 v1, Vector3 v2,
        Vector3 ro, Vector3 rd, float eps,
        ref float tMin, ref bool hit)
    {
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var h  = Vector3.Cross(rd, e2);
        float a = Vector3.Dot(e1, h);
        if (MathF.Abs(a) < eps) return;

        float f = 1f / a;
        var s   = ro - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return;

        var q   = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(rd, q);
        if (v < 0f || u + v > 1f) return;

        float t = f * Vector3.Dot(e2, q);
        if (t > BaseEps && t < tMin)
        {
            tMin = t;
            hit  = true;
        }
    }

    // -- Face picking ----------------------------------------------------------

    /// <summary>
    /// Result of a face pick: mesh leaf node, triangle index (0, 1, 2…), hit point / normal.
    /// </summary>
    public readonly record struct FaceHit(
        SceneNode MeshNode,
        SceneNode SelectableRoot,
        int TriangleIndex,
        float Distance,
        Vector3 WorldHit,
        Vector3 WorldNormal);

    /// <summary>
    /// Like <see cref="Pick"/> but also returns the world-space face normal of the
    /// closest hit triangle, oriented toward the camera (away from the ray).
    /// </summary>
    public static SceneNode? PickFace(
        Ray worldRay, SceneNode root,
        out Vector3 worldFaceNormal, out float hitDistance)
    {
        var hit = PickFaceDetailed(worldRay, root, _ => true);
        if (hit is null)
        {
            hitDistance = float.MaxValue;
            worldFaceNormal = Vector3.UnitZ;
            return null;
        }
        hitDistance = hit.Value.Distance;
        worldFaceNormal = hit.Value.WorldNormal;
        return hit.Value.MeshNode;
    }

    /// <summary>
    /// Face pick restricted to selectable roots that pass <paramref name="acceptSelectable"/>.
    /// Returns full hit info including triangle index (0-based face id).
    /// </summary>
    public static FaceHit? PickFaceDetailed(
        Ray worldRay, SceneNode root, Func<SceneNode, bool> acceptSelectable)
    {
        float hitDistance = float.MaxValue;
        SceneNode? closest = null;
        SceneNode? closestSel = null;
        Vector3 closestNormal = Vector3.UnitZ;
        int closestTri = -1;
        PickTier bestTier = PickTier.Environment;

        foreach (var node in root.SelfAndDescendants())
        {
            if (node.PickIgnore) continue;
            if (!node.Visible) continue;
            if (node.Mesh?.PickingData is not { } mesh) continue;
            if (FindSelectableRoot(node) is not { } selectable) continue;
            if (!acceptSelectable(selectable)) continue;

            Matrix4.Invert(node.WorldTransform, out var invWorld);
            var lo = TransformPoint(worldRay.Origin,    invWorld);
            var ld = TransformDir  (worldRay.Direction, invWorld);

            var (bMin, bMax) = mesh.LocalBounds;
            if (!RayHitsAabb(lo, ld, bMin, bMax)) continue;

            if (!IntersectFace(mesh, lo, ld, out float t, out Vector3 localNormal, out int tri))
                continue;

            var tier = selectable.PickTier;
            if (tier < bestTier || (tier == bestTier && t < hitDistance))
            {
                bestTier      = tier;
                hitDistance   = t;
                closest       = node;
                closestSel    = selectable;
                closestTri    = tri;
                closestNormal = TransformDir(localNormal, node.WorldTransform);
            }
        }

        if (closest is null || closestSel is null || closestTri < 0)
            return null;

        var n = closestNormal.LengthSquared > 1e-12f
            ? Vector3.Normalize(closestNormal)
            : Vector3.UnitZ;
        return new FaceHit(closest, closestSel, closestTri, hitDistance, worldRay.At(hitDistance), n);
    }

    /// <summary>
    /// Returns the three local-space corners of triangle <paramref name="triangleIndex"/>
    /// (0-based face id), or false if out of range.
    /// </summary>
    public static bool TryGetTriangleLocal(
        MeshData mesh, int triangleIndex, out Vector3 v0, out Vector3 v1, out Vector3 v2)
    {
        v0 = v1 = v2 = default;
        if (triangleIndex < 0) return false;
        var pos = mesh.Positions;
        if (mesh.Indices is { } idx)
        {
            int i = triangleIndex * 3;
            if (i + 2 >= idx.Length) return false;
            v0 = pos[idx[i]];
            v1 = pos[idx[i + 1]];
            v2 = pos[idx[i + 2]];
            return true;
        }
        int p = triangleIndex * 3;
        if (p + 2 >= pos.Length) return false;
        v0 = pos[p];
        v1 = pos[p + 1];
        v2 = pos[p + 2];
        return true;
    }

    /// <summary>Number of triangles in <paramref name="mesh"/>.</summary>
    public static int TriangleCount(MeshData mesh)
        => mesh.Indices is { } idx ? idx.Length / 3 : mesh.Positions.Length / 3;

    private static bool IntersectFace(
        MeshData mesh, Vector3 ro, Vector3 rd,
        out float tMin, out Vector3 normal, out int triangleIndex)
    {
        tMin   = float.MaxValue;
        normal = Vector3.UnitZ;
        triangleIndex = -1;
        bool    hit    = false;
        Vector3 bestE1 = default, bestE2 = default;
        var     pos    = mesh.Positions;

        // See the matching comment in Intersect(): epsilon must scale with |rd|, or a node whose
        // WorldTransform bakes in a non-unit scale (GLB's ×1000 metres->mm conversion) shrinks rd
        // enough that real hits get rejected as "parallel".
        float eps = BaseEps * rd.Length;

        if (mesh.Indices is { } idx)
        {
            for (int i = 0, tri = 0; i + 2 < idx.Length; i += 3, tri++)
            {
                if (TestTriFace(pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]], ro, rd, eps,
                                ref tMin, ref hit, ref bestE1, ref bestE2))
                    triangleIndex = tri;
            }
        }
        else
        {
            for (int i = 0, tri = 0; i + 2 < pos.Length; i += 3, tri++)
            {
                if (TestTriFace(pos[i], pos[i + 1], pos[i + 2], ro, rd, eps,
                                ref tMin, ref hit, ref bestE1, ref bestE2))
                    triangleIndex = tri;
            }
        }

        if (hit)
        {
            var n = Vector3.Cross(bestE1, bestE2);
            // Flip so the normal always faces toward the camera (against the ray).
            if (Vector3.Dot(rd, n) > 0f) n = -n;
            if (n.LengthSquared > 1e-12f) normal = Vector3.Normalize(n);
        }
        return hit;
    }

    /// <returns>True when this triangle became the new closest hit.</returns>
    private static bool TestTriFace(
        Vector3 v0, Vector3 v1, Vector3 v2,
        Vector3 ro, Vector3 rd, float eps,
        ref float tMin, ref bool hit,
        ref Vector3 bestE1, ref Vector3 bestE2)
    {
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var h  = Vector3.Cross(rd, e2);
        float a = Vector3.Dot(e1, h);
        if (MathF.Abs(a) < eps) return false;

        float f = 1f / a;
        var s   = ro - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;

        var q   = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(rd, q);
        if (v < 0f || u + v > 1f) return false;

        float t = f * Vector3.Dot(e2, q);
        if (t > BaseEps && t < tMin)
        {
            tMin   = t;
            hit    = true;
            bestE1 = e1;
            bestE2 = e2;
            return true;
        }
        return false;
    }

    // -- Row-vector transform helpers ------------------------------------------

    private static Vector3 TransformPoint(Vector3 p, Matrix4 m)
        => new(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    private static Vector3 TransformDir(Vector3 d, Matrix4 m)
        => new(
            d.X * m.M11 + d.Y * m.M21 + d.Z * m.M31,
            d.X * m.M12 + d.Y * m.M22 + d.Z * m.M32,
            d.X * m.M13 + d.Y * m.M23 + d.Z * m.M33);
}
