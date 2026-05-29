using System.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>
/// Shared logic for loading and placing user-imported 3D models.
/// Handles file loading, bounding-box computation, and bed-surface placement.
/// </summary>
internal static class ImportHelper
{
    private static readonly HashSet<string> SupportedExtensions = [".glb", ".gltf", ".stl"];

    internal static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Loads a model file and places it on the bed surface with its bounding-box
    /// centre aligned to the bed's XY centre. Returns <c>null</c> on load failure.
    /// </summary>
    internal static SceneNode? LoadAndPlace(string filePath, CellConfig? activeCell)
    {
        SceneNode node;
        try
        {
            node = filePath.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)
                ? StlLoader.Load(filePath)
                : GltfLoader.Load(filePath);
            node.CullFaces = false;
        }
        catch { return null; }

        PlaceOnBed(node, activeCell);
        return node;
    }

    /// <summary>
    /// Translates <paramref name="node"/> so its bounding-box centre XY aligns with
    /// the bed centre and its bounding-box min-Z sits on the bed surface.
    /// No-op if no geometry is found or no active cell is loaded.
    /// </summary>
    internal static void PlaceOnBed(SceneNode node, CellConfig? activeCell)
    {
        var (min, max) = ComputeSubtreeAabb(node);
        if (min.X > max.X) return;

        var bedCenter = GetBedCenter(activeCell);
        var center    = (min + max) * 0.5f;

        var lt = node.LocalTransform;
        lt.M41 += bedCenter.X - center.X;
        lt.M42 += bedCenter.Y - center.Y;
        lt.M43 += bedCenter.Z - min.Z;
        node.LocalTransform = lt;
    }

    // -- AABB ------------------------------------------------------------------

    /// <summary>
    /// Computes the world-space axis-aligned bounding box of all <see cref="MeshData"/>
    /// found in <paramref name="root"/>'s subtree. Works on freshly loaded nodes that
    /// are not yet attached to the scene graph (uses WorldTransform up to root).
    /// Returns (MaxValue, MinValue) when no geometry is found.
    /// </summary>
    private static (Vector3 Min, Vector3 Max) ComputeSubtreeAabb(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not { } mesh) continue;

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

        return (min, max);
    }

    // -- Bed helpers -----------------------------------------------------------

    private static Vector3 GetBedCenter(CellConfig? cell)
    {
        if (cell?.Bed is not { } bed) return Vector3.Zero;
        var corner = bed.GridOrigin ?? bed.Origin;
        return new Vector3(
            corner.X + bed.Width  / 2f,
            corner.Y + bed.Depth  / 2f,
            corner.Z);
    }
}
