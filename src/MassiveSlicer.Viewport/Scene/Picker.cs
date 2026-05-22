using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// CPU-side ray-mesh intersection for scene picking.
/// Tests a world-space ray against all mesh nodes in the scene graph using
/// Möller-Trumbore in each node's local space.
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
    {
        hitDistance = float.MaxValue;
        SceneNode? closest = null;

        foreach (var node in root.SelfAndDescendants())
        {
            if (node.Mesh?.PickingData is not { } mesh) continue;
            if (!IsPickable(node, root)) continue;

            Matrix4.Invert(node.WorldTransform, out var invWorld);
            var lo = TransformPoint(worldRay.Origin,    invWorld);
            var ld = TransformDir  (worldRay.Direction, invWorld);

            // Cheap AABB pre-reject before per-triangle Möller-Trumbore.
            var (bMin, bMax) = mesh.LocalBounds;
            if (!RayHitsAabb(lo, ld, bMin, bMax)) continue;

            // t in local space == t in world space (direction pre-scaled by invWorld)
            if (Intersect(mesh, lo, ld, out float t) && t < hitDistance)
            {
                hitDistance = t;
                closest     = node;
            }
        }

        return closest;
    }

    // Returns true when the nearest SceneRoot-child ancestor of node is selectable.
    private static bool IsPickable(SceneNode node, SceneNode sceneRoot)
    {
        var current = node;
        while (current is not null)
        {
            if (current.Parent == sceneRoot) return current.Selectable;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Walks the parent chain of <paramref name="node"/> to find the direct child of
    /// <paramref name="sceneRoot"/> — the logical selectable object in the outliner.
    /// </summary>
    public static SceneNode? FindSelectableRoot(SceneNode node, SceneNode sceneRoot)
    {
        var current = node;
        while (current is not null)
        {
            if (current.Parent == sceneRoot)
                return current.Selectable ? current : null;
            current = current.Parent;
        }
        return null;
    }

    // ── Ray-AABB slab test ────────────────────────────────────────────────────

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

    // ── Möller-Trumbore ───────────────────────────────────────────────────────

    private static bool Intersect(MeshData mesh, Vector3 ro, Vector3 rd, out float tMin)
    {
        tMin = float.MaxValue;
        bool hit = false;
        var pos = mesh.Positions;

        if (mesh.Indices is { } idx)
        {
            for (int i = 0; i + 2 < idx.Length; i += 3)
                TestTri(pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]], ro, rd, ref tMin, ref hit);
        }
        else
        {
            for (int i = 0; i + 2 < pos.Length; i += 3)
                TestTri(pos[i], pos[i + 1], pos[i + 2], ro, rd, ref tMin, ref hit);
        }

        return hit;
    }

    private static void TestTri(
        Vector3 v0, Vector3 v1, Vector3 v2,
        Vector3 ro, Vector3 rd,
        ref float tMin, ref bool hit)
    {
        const float Eps = 1e-6f;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var h  = Vector3.Cross(rd, e2);
        float a = Vector3.Dot(e1, h);
        if (MathF.Abs(a) < Eps) return;

        float f = 1f / a;
        var s   = ro - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return;

        var q   = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(rd, q);
        if (v < 0f || u + v > 1f) return;

        float t = f * Vector3.Dot(e2, q);
        if (t > Eps && t < tMin)
        {
            tMin = t;
            hit  = true;
        }
    }

    // ── Face picking ──────────────────────────────────────────────────────────

    /// <summary>
    /// Like <see cref="Pick"/> but also returns the world-space face normal of the
    /// closest hit triangle, oriented toward the camera (away from the ray).
    /// </summary>
    public static SceneNode? PickFace(
        Ray worldRay, SceneNode root,
        out Vector3 worldFaceNormal, out float hitDistance)
    {
        hitDistance    = float.MaxValue;
        worldFaceNormal = Vector3.UnitZ;
        SceneNode? closest     = null;
        Vector3   closestNormal = Vector3.UnitZ;

        foreach (var node in root.SelfAndDescendants())
        {
            if (node.Mesh?.PickingData is not { } mesh) continue;
            if (!IsPickable(node, root)) continue;

            Matrix4.Invert(node.WorldTransform, out var invWorld);
            var lo = TransformPoint(worldRay.Origin,    invWorld);
            var ld = TransformDir  (worldRay.Direction, invWorld);

            var (bMin, bMax) = mesh.LocalBounds;
            if (!RayHitsAabb(lo, ld, bMin, bMax)) continue;

            if (IntersectFace(mesh, lo, ld, out float t, out Vector3 localNormal) && t < hitDistance)
            {
                hitDistance   = t;
                closest       = node;
                closestNormal = TransformDir(localNormal, node.WorldTransform);
            }
        }

        if (closest is not null && closestNormal.LengthSquared > 1e-12f)
            worldFaceNormal = Vector3.Normalize(closestNormal);
        return closest;
    }

    private static bool IntersectFace(
        MeshData mesh, Vector3 ro, Vector3 rd,
        out float tMin, out Vector3 normal)
    {
        tMin   = float.MaxValue;
        normal = Vector3.UnitZ;
        bool    hit    = false;
        Vector3 bestE1 = default, bestE2 = default;
        var     pos    = mesh.Positions;

        if (mesh.Indices is { } idx)
        {
            for (int i = 0; i + 2 < idx.Length; i += 3)
                TestTriFace(pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]], ro, rd,
                            ref tMin, ref hit, ref bestE1, ref bestE2);
        }
        else
        {
            for (int i = 0; i + 2 < pos.Length; i += 3)
                TestTriFace(pos[i], pos[i + 1], pos[i + 2], ro, rd,
                            ref tMin, ref hit, ref bestE1, ref bestE2);
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

    private static void TestTriFace(
        Vector3 v0, Vector3 v1, Vector3 v2,
        Vector3 ro, Vector3 rd,
        ref float tMin, ref bool hit,
        ref Vector3 bestE1, ref Vector3 bestE2)
    {
        const float Eps = 1e-6f;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var h  = Vector3.Cross(rd, e2);
        float a = Vector3.Dot(e1, h);
        if (MathF.Abs(a) < Eps) return;

        float f = 1f / a;
        var s   = ro - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return;

        var q   = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(rd, q);
        if (v < 0f || u + v > 1f) return;

        float t = f * Vector3.Dot(e2, q);
        if (t > Eps && t < tMin)
        {
            tMin   = t;
            hit    = true;
            bestE1 = e1;
            bestE2 = e2;
        }
    }

    // ── Row-vector transform helpers ──────────────────────────────────────────

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
