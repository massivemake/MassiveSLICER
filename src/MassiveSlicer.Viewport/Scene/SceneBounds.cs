using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>World-space axis-aligned bounds for scene subtrees.</summary>
public static class SceneBounds
{
    /// <summary>
    /// Computes the world-space AABB of all visible mesh geometry under <paramref name="root"/>.
    /// Uses uploaded <see cref="SceneNode.Mesh"/> picking data or pending CPU meshes.
    /// </summary>
    public static bool TryComputeSubtreeWorldAabb(SceneNode root, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        var any = false;

        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var n in root.SelfAndDescendants())
        {
            if (!n.Visible) continue;

            var mesh = n.Mesh?.PickingData ?? n.PendingMesh;
            if (mesh is null) continue;

            any = true;
            var world        = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;

            corners[0] = new(bMin.X, bMin.Y, bMin.Z); corners[1] = new(bMax.X, bMin.Y, bMin.Z);
            corners[2] = new(bMin.X, bMax.Y, bMin.Z); corners[3] = new(bMax.X, bMax.Y, bMin.Z);
            corners[4] = new(bMin.X, bMin.Y, bMax.Z); corners[5] = new(bMax.X, bMin.Y, bMax.Z);
            corners[6] = new(bMin.X, bMax.Y, bMax.Z); corners[7] = new(bMax.X, bMax.Y, bMax.Z);

            foreach (var p in corners)
            {
                var w = new Vector3(
                    p.X * world.M11 + p.Y * world.M21 + p.Z * world.M31 + world.M41,
                    p.X * world.M12 + p.Y * world.M22 + p.Z * world.M32 + world.M42,
                    p.X * world.M13 + p.Y * world.M23 + p.Z * world.M33 + world.M43);
                min = Vector3.ComponentMin(min, w);
                max = Vector3.ComponentMax(max, w);
            }
        }

        return any;
    }

    /// <summary>Lowest world Z among all visible mesh geometry in <paramref name="sceneRoot"/>.</summary>
    public static bool TryComputeSceneFloorZ(SceneNode sceneRoot, out float floorZ)
    {
        floorZ = float.MaxValue;
        var any = false;

        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var n in sceneRoot.SelfAndDescendants())
        {
            if (!n.Visible) continue;

            var mesh = n.Mesh?.PickingData ?? n.PendingMesh;
            if (mesh is null) continue;

            any = true;
            var world        = n.WorldTransform;
            var (bMin, bMax) = mesh.LocalBounds;

            corners[0] = new(bMin.X, bMin.Y, bMin.Z); corners[1] = new(bMax.X, bMin.Y, bMin.Z);
            corners[2] = new(bMin.X, bMax.Y, bMin.Z); corners[3] = new(bMax.X, bMax.Y, bMin.Z);
            corners[4] = new(bMin.X, bMin.Y, bMax.Z); corners[5] = new(bMax.X, bMin.Y, bMax.Z);
            corners[6] = new(bMin.X, bMax.Y, bMax.Z); corners[7] = new(bMax.X, bMax.Y, bMax.Z);

            foreach (var p in corners)
            {
                var w = new Vector3(
                    p.X * world.M11 + p.Y * world.M21 + p.Z * world.M31 + world.M41,
                    p.X * world.M12 + p.Y * world.M22 + p.Z * world.M32 + world.M42,
                    p.X * world.M13 + p.Y * world.M23 + p.Z * world.M33 + world.M43);
                floorZ = MathF.Min(floorZ, w.Z);
            }
        }

        return any;
    }
}