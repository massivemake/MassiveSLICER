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

    /// <summary>
    /// Reloads disk geometry into an existing scene node, preserving its transform and scene identity.
    /// GPU meshes must be released and re-uploaded on the GL thread after this call.
    /// </summary>
    internal static bool TryReloadInto(SceneNode target, string filePath)
    {
        var loaded = LoadFile(filePath);
        if (loaded is null) return false;

        var localTransform = target.LocalTransform;
        var visible        = target.Visible;
        var selectable     = target.Selectable;
        var cullFaces      = target.CullFaces;
        var layerPreview   = target.LayerPreview;
        var pickTier       = target.PickTier;
        var name           = target.Name;
        var overlay        = target.Overlay;

        foreach (var child in target.Children.ToList())
            target.RemoveChild(child);

        target.Mesh        = null;
        target.PendingMesh = null;

        if (loaded.PendingMesh is { } pending)
        {
            target.PendingMesh = pending;
            loaded.PendingMesh = null;
        }

        foreach (var child in loaded.Children.ToList())
        {
            loaded.RemoveChild(child);
            target.AddChild(child);
        }

        target.LocalTransform = localTransform;
        target.Visible        = visible;
        target.Selectable     = selectable;
        target.CullFaces      = cullFaces;
        target.LayerPreview   = layerPreview;
        target.PickTier       = pickTier;
        target.Name           = name;
        target.Overlay        = overlay;
        target.SourceFilePath = Path.GetFullPath(filePath);
        return true;
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

    /// <summary>
    /// Moves the node origin to the bottom-center of its mesh bounds while keeping world geometry fixed.
    /// Bakes per-mesh vertex offsets and compensates via the root transform; callers must re-upload GPU meshes.
    /// </summary>
    internal static bool RecenterPivotToBottomCenter(SceneNode root)
    {
        if (!TryComputeBottomCenterLocal(root, out var bottomCenter))
            return false;

        if (!IsFinite(bottomCenter))
            return false;

        int expected = CountEditableMeshes(root);
        if (expected == 0)
            return false;

        int moved = OffsetSubtreeMeshPositionsInRootLocal(root, -bottomCenter);
        if (moved != expected)
            return false;

        root.LocalTransform = root.LocalTransform * Matrix4.CreateTranslation(bottomCenter);
        return true;
    }

    internal static Dictionary<SceneNode, Matrix4> SnapshotSubtreeTransforms(SceneNode root)
    {
        var snap = new Dictionary<SceneNode, Matrix4>();
        foreach (var n in root.SelfAndDescendants())
            snap[n] = n.LocalTransform;
        return snap;
    }

    internal static void RestoreSubtreeTransforms(SceneNode root, Dictionary<SceneNode, Matrix4> transforms)
    {
        foreach (var n in root.SelfAndDescendants())
        {
            if (transforms.TryGetValue(n, out var local))
                n.LocalTransform = local;
        }
    }

    internal static Dictionary<SceneNode, MeshData?> SnapshotSubtreeMeshes(SceneNode root)
    {
        var snap = new Dictionary<SceneNode, MeshData?>();
        foreach (var n in root.SelfAndDescendants())
            snap[n] = CloneMeshSnapshot(n);
        return snap;
    }

    internal static void RestoreMeshSnapshot(SceneNode node, MeshData? mesh)
    {
        if (mesh is null)
        {
            node.PendingMesh = null;
            return;
        }

        node.PendingMesh = CloneMeshData(mesh);
    }

    internal static void RestoreSubtreeSnapshot(
        SceneNode root,
        Dictionary<SceneNode, Matrix4> transforms,
        Dictionary<SceneNode, MeshData?> meshes)
    {
        RestoreSubtreeTransforms(root, transforms);
        foreach (var (node, mesh) in meshes)
            RestoreMeshSnapshot(node, mesh);
    }

    private static MeshData? CloneMeshSnapshot(SceneNode node)
    {
        if (node.PendingMesh is { } pending) return CloneMeshData(pending);
        if (node.Mesh?.PickingData is { } gpu) return CloneMeshData(gpu);
        return null;
    }

    private static MeshData CloneMeshData(MeshData mesh) =>
        new(mesh.Positions.ToArray(), mesh.Normals.ToArray(), mesh.Indices?.ToArray() ?? [], mesh.Name,
            mesh.BaseColor, mesh.Metallic, mesh.Roughness,
            mesh.Uvs?.ToArray(), mesh.Tangents?.ToArray(), mesh.Material);

    private static bool TryComputeBottomCenterLocal(SceneNode root, out Vector3 bottomCenterLocal)
    {
        bottomCenterLocal = default;
        var (wMin, wMax)  = ComputeSubtreeWorldAabb(root);
        if (wMin.X > wMax.X) return false;

        var bcWorld = new Vector3(
            (wMin.X + wMax.X) * 0.5f,
            (wMin.Y + wMax.Y) * 0.5f,
            wMin.Z);

        bottomCenterLocal = TransformPoint(bcWorld, root.WorldTransform.Inverted());
        return true;
    }

    private static int OffsetSubtreeMeshPositionsInRootLocal(SceneNode root, Vector3 rootLocalOffset)
    {
        var toRootFromMesh = root.WorldTransform.Inverted();
        int moved = 0;
        foreach (var n in root.SelfAndDescendants())
        {
            var mesh = GetOrCloneEditableMesh(n);
            if (mesh is null) continue;

            var meshToRoot = n.WorldTransform * toRootFromMesh;
            var shift      = TransformPoint(rootLocalOffset, meshToRoot.Inverted());
            if (!IsFinite(shift)) continue;

            for (int i = 0; i < mesh.Positions.Length; i++)
                mesh.Positions[i] += shift;

            n.PendingMesh = CloneMeshData(mesh);
            moved++;
        }
        return moved;
    }

    private static MeshData? GetOrCloneEditableMesh(SceneNode node)
    {
        if (node.PendingMesh is { } pending) return pending;
        if (node.Mesh?.PickingData is not { } gpu)
            return null;

        var clone = CloneMeshData(gpu);
        node.PendingMesh = clone;
        return clone;
    }

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static int CountEditableMeshes(SceneNode root)
    {
        int count = 0;
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.PendingMesh is not null || n.Mesh?.PickingData is not null)
                count++;
        }
        return count;
    }

    private static Vector3 TransformPoint(Vector3 p, Matrix4 m)
        => new(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

    private static (Vector3 Min, Vector3 Max) ComputeSubtreeWorldAabb(SceneNode root)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;

        foreach (var n in root.SelfAndDescendants())
        {
            var mesh = n.Mesh?.PickingData ?? n.PendingMesh;
            if (mesh is null || mesh.Positions.Length == 0) continue;

            any = true;
            var world = n.WorldTransform;
            foreach (var p in mesh.Positions)
            {
                var w = TransformPoint(p, world);
                min = Vector3.ComponentMin(min, w);
                max = Vector3.ComponentMax(max, w);
            }
        }

        return any ? (min, max) : (new Vector3(float.MaxValue), new Vector3(float.MinValue));
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