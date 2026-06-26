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
    private static readonly HashSet<string> SupportedExtensions = [".glb", ".gltf", ".stl", ".obj", ".3mf"];

    /// <summary>Fraction of rotary radius used when scaling oversized imports to fit the table.</summary>
    private const float RotaryFitMargin = 0.96f;

    internal static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Loads a model file and places it on the bed surface with its bounding-box
    /// centre aligned to the bed's XY centre. Returns <c>null</c> on load failure.
    /// </summary>
    internal static SceneNode? LoadAndPlace(string filePath, CellConfig? activeCell)
    {
        var node = LoadFile(filePath);
        if (node is null) return null;

        PlaceOnBed(node, activeCell);
        return node;
    }

    /// <summary>
    /// Loads a model file and applies <paramref name="localTransform"/> without bed placement.
    /// Used when restoring a saved workspace.
    /// </summary>
    internal static SceneNode? LoadAtTransform(string filePath, Matrix4 localTransform)
    {
        var node = LoadFile(filePath);
        if (node is null) return null;

        node.LocalTransform = localTransform;
        return node;
    }

    private static SceneNode? LoadFile(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var node = ext switch
            {
                ".stl" => StlLoader.Load(filePath),
                ".obj" => ObjLoader.Load(filePath),
                ".3mf" => ThreeMfLoader.Load(filePath),
                _      => GltfLoader.Load(filePath),
            };
            node.CullFaces     = false;
            node.SourceFilePath = Path.GetFullPath(filePath);
            return node;
        }
        catch { return null; }
    }

    /// <summary>
    /// Translates <paramref name="node"/> so its bounding-box centre XY aligns with the
    /// cell's import surface centre and its bounding-box min-Z sits on that surface.
    /// LFAM 3 (rotary): centres on <c>bed.origin</c> and scales down to fit the table diameter.
    /// LFAM 2 (rectangular): centres on the print-bed grid footprint.
    /// No-op if no geometry is found or no active cell is loaded.
    /// </summary>
    internal static void PlaceOnBed(SceneNode node, CellConfig? activeCell)
    {
        if (activeCell?.Bed is not { } bed) return;

        var surface = bed.ImportSurfaceCenter(activeCell.Robot.WorldPosition);
        var bedCenter = new Vector3(surface.X, surface.Y, surface.Z);

        if (bed.ImportSurfaceRadiusMm is { } radius)
            ScaleToFitWithinRadius(node, radius * RotaryFitMargin);

        var (min, max) = ComputeSubtreeAabb(node);
        if (min.X > max.X) return;

        var center = (min + max) * 0.5f;

        var lt = node.LocalTransform;
        lt.M41 += bedCenter.X - center.X;
        lt.M42 += bedCenter.Y - center.Y;
        lt.M43 += bedCenter.Z - min.Z;
        node.LocalTransform = lt;
    }

    /// <summary>Uniformly scales <paramref name="node"/> so its XY footprint fits in a circle.</summary>
    private static void ScaleToFitWithinRadius(SceneNode node, float maxRadius)
    {
        var (min, max) = ComputeSubtreeAabb(node);
        if (min.X > max.X) return;

        var c = (min + max) * 0.5f;
        float half = MathF.Max(
            MathF.Max(max.X - c.X, c.X - min.X),
            MathF.Max(max.Y - c.Y, c.Y - min.Y));
        if (half <= maxRadius) return;

        float s = maxRadius / half;
        var pre = node.LocalTransform;
        node.LocalTransform =
            Matrix4.CreateTranslation(c) *
            Matrix4.CreateScale(s) *
            Matrix4.CreateTranslation(-c) *
            pre;
    }

    /// <summary>
    /// Recenters native Y-up metre stand geometry so the wrapper origin sits at the
    /// stand base centre. Matches MassiveCONNECT <c>robots.html</c> (-cx, -minY, -cz).
    /// </summary>
    internal static SceneNode RecenterStandYup(SceneNode nativeRoot)
    {
        var (min, max) = ComputeSubtreeAabb(nativeRoot);
        if (min.X > max.X) return nativeRoot;

        var center = (min + max) * 0.5f;
        var recenter = new SceneNode
        {
            Name           = nativeRoot.Name + "_Recenter",
            LocalTransform = Matrix4.CreateTranslation(-center.X, -min.Y, -center.Z),
            Selectable     = false,
        };

        foreach (var child in nativeRoot.Children.ToList())
        {
            nativeRoot.RemoveChild(child);
            recenter.AddChild(child);
        }

        nativeRoot.AddChild(recenter);
        return nativeRoot;
    }

    // -- AABB ------------------------------------------------------------------

    /// <summary>
    /// Computes the world-space axis-aligned bounding box of all <see cref="MeshData"/>
    /// found in <paramref name="root"/>'s subtree. Works on freshly loaded nodes that
    /// are not yet attached to the scene graph (uses WorldTransform up to root).
    /// Returns (MaxValue, MinValue) when no geometry is found.
    /// </summary>
    internal static (Vector3 Min, Vector3 Max) ComputeSubtreeAabb(SceneNode root)
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
}